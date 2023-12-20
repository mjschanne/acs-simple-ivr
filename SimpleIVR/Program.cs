using Azure.Communication;
using Azure.Communication.CallAutomation;
using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Azure.Messaging.EventGrid.SystemEvents;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Newtonsoft.Json;
using SimpleIVR.Models;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

var devTunnelUri = builder.Configuration["DevTunnelUri"];
var acsConnectionString = builder.Configuration["AcsConnectionString"];
var maxTimeout = 2;

// There is another overload of this constructor that support token credential which means you should be able to use managed identity if that is a requirement
//      CallAutomationClient(Uri endpoint, TokenCredential credential, CallAutomationClientOptions options = default)
// For simplicity, we are using connection string here
var client = new CallAutomationClient(connectionString: acsConnectionString);

builder.Services.AddSingleton(client);

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.MapPost("/api/incomingCall", async ([FromBody] EventGridEvent[] eventGridEvents, ILogger<Program> logger) =>
{
    foreach (var eventGridEvent in eventGridEvents)
    {
        logger.LogInformation($"Incoming call event received.");

        // Handle the subscription validation event.
        if (eventGridEvent.TryGetSystemEventData(out object eventData) && eventData is SubscriptionValidationEventData subscriptionValidationEventData)
            return Results.Ok(new SubscriptionValidationResponse
            {
                ValidationResponse = subscriptionValidationEventData.ValidationCode
            });

        var incomingCallEvent = JsonConvert.DeserializeObject<IncomingCallEvent>(JsonNode.Parse(eventGridEvent.Data).ToJsonString());

        var callbackUri = new Uri(new Uri(devTunnelUri), $"/api/callbacks/{Guid.NewGuid()}?callerId={incomingCallEvent.From.RawId}");

        var options = new AnswerCallOptions(incomingCallEvent.IncomingCallContext, callbackUri);

        AnswerCallResult answerCallResult = await client.AnswerCallAsync(options);

        logger.LogInformation($"Answered call for connection id: {answerCallResult.CallConnection.CallConnectionId}");

        // Use EventProcessor to process CallConnected event
        // https://learn.microsoft.com/en-us/azure/communication-services/how-tos/call-automation/handle-events-with-event-processor
        // event codes list: https://learn.microsoft.com/en-us/azure/communication-services/how-tos/call-automation/recognize-action?pivots=programming-language-csharp#event-codes
        var answer_result = await answerCallResult.WaitForEventProcessorAsync();

        if (answer_result.IsSuccess)
        {
            logger.LogInformation($"Call connected event received for connection id: {answer_result.SuccessResult.CallConnectionId}");
            var callConnectionMedia = answerCallResult.CallConnection.GetCallMedia();

            await HandleRecognizeAsync(callConnectionMedia, incomingCallEvent.From.RawId, "mainmenu");
        }

        // at this point we've stopped recognizing, and have finished playing our message so we can hangup (or transfer to a live agent...)
        client.GetEventProcessor().AttachOngoingEventProcessor<PlayCompleted>(answerCallResult.CallConnection.CallConnectionId, async (playCompletedEvent) =>
        {
            logger.LogInformation($"Play completed event received for connection id: {playCompletedEvent.CallConnectionId}.");

            await answerCallResult.CallConnection.HangUpAsync(true);
        });

        client.GetEventProcessor().AttachOngoingEventProcessor<PlayFailed>(answerCallResult.CallConnection.CallConnectionId, async (playFailedEvent) =>
        {
            logger.LogInformation($"Play failed event received for connection id: {playFailedEvent.CallConnectionId}. Hanging up call...");

            await answerCallResult.CallConnection.HangUpAsync(true);
        });

        client.GetEventProcessor().AttachOngoingEventProcessor<CallTransferAccepted>(answerCallResult.CallConnection.CallConnectionId, async (callTransferAcceptedEvent) =>
        {
            logger.LogInformation($"Call transfer accepted event received for connection id: {callTransferAcceptedEvent.CallConnectionId}.");
        });

        client.GetEventProcessor().AttachOngoingEventProcessor<CallTransferFailed>(answerCallResult.CallConnection.CallConnectionId, async (callTransferFailedEvent) =>
        {
            logger.LogInformation($"Call transfer failed event received for connection id: {callTransferFailedEvent.CallConnectionId}.");

            var resultInformation = callTransferFailedEvent.ResultInformation;

            logger.LogError("Encountered error during call transfer, message={msg}, code={code}, subCode={subCode}", resultInformation?.Message, resultInformation?.Code, resultInformation?.SubCode);
        });

        client.GetEventProcessor().AttachOngoingEventProcessor<RecognizeCompleted>(answerCallResult.CallConnection.CallConnectionId, async (recognizeCompletedEvent) =>
        {
            logger.LogInformation($"Recognize completed event received for connection id: {recognizeCompletedEvent.CallConnectionId}");

            var dtmfResult = recognizeCompletedEvent.RecognizeResult as DtmfResult;

            var tone = dtmfResult.Tones.DefaultIfEmpty(DtmfTone.Five).FirstOrDefault();

            if (tone == DtmfTone.One)
            {
                await HandleRecognizeAsync(answerCallResult.CallConnection.GetCallMedia(), incomingCallEvent.From.RawId, "sales");
                return;
            }
            else if (tone == DtmfTone.Two)
            {
                await HandleRecognizeAsync(answerCallResult.CallConnection.GetCallMedia(), incomingCallEvent.From.RawId, "marketing");
                return;
            }
            else if (tone == DtmfTone.Three)
            {
                await HandleRecognizeAsync(answerCallResult.CallConnection.GetCallMedia(), incomingCallEvent.From.RawId, "customercare");
                return;
            }
            else if (tone == DtmfTone.Four)
            {
                await HandleRecognizeAsync(answerCallResult.CallConnection.GetCallMedia(), incomingCallEvent.From.RawId, "agent");
                // todo: look at the other sample for job routing...
                return;
            }
            else
            {
                await HandlePlayAsync(answerCallResult.CallConnection.GetCallMedia(), incomingCallEvent.From.RawId, "invalid");
                return;
            }
        });

        client.GetEventProcessor().AttachOngoingEventProcessor<RecognizeFailed>(answerCallResult.CallConnection.CallConnectionId, async (recognizeFailedEvent) =>
        {
            var callConnectionMedia = answerCallResult.CallConnection.GetCallMedia();

            // calculate what the initial silence timeout should be before reprompting, check to see how many times we have retried, then send them to a live agent if we have retried too many times
            if (MediaEventReasonCode.RecognizeInitialSilenceTimedOut.Equals(recognizeFailedEvent.ResultInformation.SubCode.Value.ToString()) && maxTimeout > 0)
            {
                logger.LogWarning($"Recognize failed event received for connection id: {recognizeFailedEvent.CallConnectionId}. Retrying recognize...");
                maxTimeout--;
                await HandleRecognizeAsync(callConnectionMedia, incomingCallEvent.From.RawId, "mainmenu"); // you could check to see
            }
            else
            {
                // after the max timeout is hit, you can try instead perform some live gent hand off here 
                logger.LogInformation($"Recognize failed event received for connection id: {recognizeFailedEvent.CallConnectionId}. Reason Code: {recognizeFailedEvent.ReasonCode}");


                var hangupTask = answerCallResult.CallConnection.HangUpAsync(true);
                await HandlePlayAsync(answerCallResult.CallConnection.GetCallMedia(), incomingCallEvent.From.RawId, "invalid");
            }
        });
    }

    return Results.Ok();
});

app.MapPost("/api/callbacks/{contextId}", async ([FromBody] CloudEvent[] cloudEvents, [FromRoute] string contextId, [Required] string callerId, CallAutomationClient callAutomationClient, ILogger<Program> logger) =>
{
    var eventProcessor = client.GetEventProcessor();

    eventProcessor.ProcessEvents(cloudEvents);

    return Results.Ok();
});

async Task HandleRecognizeAsync(CallMedia callConnectionMedia, string callerId, string wavFileName)
{
    var recognizeOptions = new CallMediaRecognizeDtmfOptions(CommunicationIdentifier.FromRawId(callerId), 1)
    {
        InterruptPrompt = false,
        InitialSilenceTimeout = TimeSpan.FromSeconds(5),
        InterToneTimeout = TimeSpan.FromSeconds(2),
        Prompt = new FileSource(new Uri(devTunnelUri.TrimEnd(new[] { '/' }) + "/audio/" + wavFileName + ".wav")),
        OperationContext = "MainMenu"
    };

    // stop recognizing on * key
    // I think you can use this initiate recgonize completed event? then you can use that for either menu navigation or to transfer to a live agent?
    recognizeOptions.StopTones.Add(DtmfTone.Asterisk);

    // have the client start trying to recognize user inputs e.g. dtmf tones or you could do sp
    var recognize_result = await callConnectionMedia.StartRecognizingAsync(recognizeOptions);
}

async Task HandlePlayAsync(CallMedia callConnectionMedia, string context, string wavFileName)
{
    var playSource = new FileSource(new Uri(devTunnelUri.TrimEnd(new[] { '/' }) + "/audio/" + wavFileName + ".wav"));
    var playOptions = new PlayToAllOptions(playSource) { OperationContext = context };
    await callConnectionMedia.PlayToAllAsync(playOptions);
}

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(Path.Combine(builder.Environment.ContentRootPath, "audio")),
    RequestPath = "/audio"
});

app.Run();