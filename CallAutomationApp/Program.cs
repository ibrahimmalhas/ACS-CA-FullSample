using Azure.Communication;
using Azure.Communication.CallAutomation;
using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Azure.Messaging.EventGrid.SystemEvents;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Newtonsoft.Json;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Your ACS resource connection string
var acsConnectionString = "";

// Your ACS resource phone number will act as source number to start outbound call
var acsPhonenumber = "";

// Target phone number you want to receive the call.
var targetPhonenumber = "";

// Base url of the app
var callbackUriHost = "";

var ParticipantToAdd = "";

var callerId = "";
var incomingCallContext = "";
CallConnection callConnection = null;

// This will be set by fileStatus endpoints
string recordingLocation = "";

string recordingId = "";

CallAutomationClient callAutomationClient = new CallAutomationClient(acsConnectionString);
var app = builder.Build();

app.MapPost("/outboundCall", async (ILogger<Program> logger) =>
{
    PhoneNumberIdentifier target = new PhoneNumberIdentifier(targetPhonenumber);
    PhoneNumberIdentifier caller = new PhoneNumberIdentifier(acsPhonenumber);
    CallInvite callInvite = new CallInvite(target, caller);
    CreateCallResult createCallResult = await callAutomationClient.CreateCallAsync(callInvite, new Uri(callbackUriHost + "/api/callbacks"));

    logger.LogInformation($"Created call with connection id: {createCallResult.CallConnectionProperties.CallConnectionId}");
    logger.LogInformation($"Created call with serverCall id: {createCallResult.CallConnectionProperties.ServerCallId}");
    logger.LogInformation($"Created call with correlation id: {createCallResult.CallConnectionProperties.CorrelationId}");

});

app.MapPost("/api/callbacks", async (CloudEvent[] cloudEvents, ILogger<Program> logger) =>
{
    foreach (var cloudEvent in cloudEvents)
    {
        CallAutomationEventBase parsedEvent = CallAutomationEventParser.Parse(cloudEvent);
        logger.LogInformation($"{parsedEvent?.GetType().Name} parsedEvent received for call connection id: {parsedEvent?.CallConnectionId}");
        logger.LogInformation($"{parsedEvent?.GetType().Name} parsedEvent received for call correlation id: {parsedEvent?.CorrelationId}");
        callConnection = callAutomationClient.GetCallConnection(parsedEvent.CallConnectionId);
        var callMedia = callConnection.GetCallMedia();

        if (parsedEvent is CallConnected)
        {
            logger.LogInformation($"Start Recording...");
            CallLocator callLocator = new ServerCallLocator(parsedEvent.ServerCallId);
            var recordingResult = await callAutomationClient.GetCallRecording().StartAsync(new StartRecordingOptions(callLocator));
            recordingId = recordingResult.Value.RecordingId;

            // prepare recognize tones
            CallMediaRecognizeDtmfOptions callMediaRecognizeDtmfOptions = new CallMediaRecognizeDtmfOptions(new PhoneNumberIdentifier(targetPhonenumber), maxTonesToCollect: 1);
            callMediaRecognizeDtmfOptions.Prompt = new FileSource(new Uri(callbackUriHost + "/audio/MainMenu.wav"));
            callMediaRecognizeDtmfOptions.InterruptPrompt = true;
            callMediaRecognizeDtmfOptions.InitialSilenceTimeout = TimeSpan.FromSeconds(5);

            // Send request to recognize tones
            await callMedia.StartRecognizingAsync(callMediaRecognizeDtmfOptions);
        }
        if (parsedEvent is RecognizeCompleted recognizeCompleted)
        {
            // Play audio once recognition is completed sucessfully
            string selectedTone = ((DtmfResult)recognizeCompleted.RecognizeResult).ConvertToString();

            switch (selectedTone)
            {
                case "1":
                    await callMedia.PlayToAllAsync(new FileSource(new Uri(callbackUriHost + "/audio/Confirmed.wav")));
                    break;

                case "2":
                    await callMedia.PlayToAllAsync(new FileSource(new Uri(callbackUriHost + "/audio/Goodbye.wav")));
                    break;

                default:
                    //invalid tone
                    await callMedia.PlayToAllAsync(new FileSource(new Uri(callbackUriHost + "/audio/Invalid.wav")));
                    break;
            }
        }
        if (parsedEvent is RecognizeFailed recognizeFailedEvent)
        {
            logger.LogInformation($"RecognizeFailed parsedEvent received for call connection id: {parsedEvent.CallConnectionId}");

            // Check for time out, and then play audio message
            if (recognizeFailedEvent.ReasonCode.Equals(MediaEventReasonCode.RecognizeInitialSilenceTimedOut))
            {
                await callMedia.PlayToAllAsync(new FileSource(new Uri(callbackUriHost + "/audio/Timeout.wav")));
            }
        }
        if ((parsedEvent is PlayCompleted) || (parsedEvent is PlayFailed))
        {
            logger.LogInformation($"Stop recording and terminating call.");
            callAutomationClient.GetCallRecording().Stop(recordingId);
            await callConnection.HangUpAsync(true);
        }
    }
    return Results.Ok();
}).Produces(StatusCodes.Status200OK);

app.MapPost("/api/recordingFileStatus", (EventGridEvent[] eventGridEvents, ILogger<Program> logger) =>
{
    foreach (var eventGridEvent in eventGridEvents)
    {
        if (eventGridEvent.TryGetSystemEventData(out object eventData))
        {
            if (eventData is SubscriptionValidationEventData subscriptionValidationEventData)
            {
                var responseData = new SubscriptionValidationResponse
                {
                    ValidationResponse = subscriptionValidationEventData.ValidationCode
                };
                return Results.Ok(responseData);
            }
            if (eventData is AcsRecordingFileStatusUpdatedEventData statusUpdated)
            {
                recordingLocation = statusUpdated.RecordingStorageInfo.RecordingChunks[0].ContentLocation;
                logger.LogInformation($"The recording location is : {recordingLocation}");
            }
        }
    }
    return Results.Ok();
});
//Incoming Call
app.MapPost("/api/incomingCall", async (
    [FromBody] EventGridEvent[] eventGridEvents,
    ILogger<Program> logger) =>
{
    foreach (var eventGridEvent in eventGridEvents)
    {
        logger.LogInformation($"Incoming Call event received : {JsonConvert.SerializeObject(eventGridEvent)}");
        // Handle system events
        if (eventGridEvent.TryGetSystemEventData(out object eventData))
        {
            // Handle the subscription validation event.
            if (eventData is SubscriptionValidationEventData subscriptionValidationEventData)
            {
                var responseData = new SubscriptionValidationResponse
                {
                    ValidationResponse = subscriptionValidationEventData.ValidationCode
                };
                return Results.Ok(responseData);
            }
        }
        var jsonObject = JsonNode.Parse(eventGridEvent.Data).AsObject();
        callerId = (string)(jsonObject["from"]["rawId"]);
        incomingCallContext = (string)jsonObject["incomingCallContext"];
        logger.LogInformation($"JSONOBJECT: {eventGridEvent.Data}");
    }
    return Results.Ok();
});

//Answer Call
app.MapPost("api/answerCall", async (
    [FromBody] EventGridEvent[] eventGridEvents,
    ILogger<Program> logger) =>
{
    foreach (var eventGridEvent in eventGridEvents)
    {
        var jsonObject = JsonNode.Parse(eventGridEvent.Data).AsObject();
        var callbackUri = new Uri(callbackUriHost + $"/api/calls/{Guid.NewGuid()}?callerId={callerId}");

        AnswerCallResult answerCallResult = await callAutomationClient.AnswerCallAsync(incomingCallContext, callbackUri);
        callConnection = answerCallResult.CallConnection;
    }
    return Results.Ok();
});

app.MapPost("/api/calls/{contextId}", async (
    [FromBody] CloudEvent[] cloudEvents,
    [FromRoute] string contextId,
    [Required] string callerId,
    ILogger<Program> logger) =>
{

    if (callerId.StartsWith('4'))
    {
        callerId = callerId.Replace(' ', '+');
    }

    foreach (var cloudEvent in cloudEvents)
    {
        CallAutomationEventBase parsedEvent = CallAutomationEventParser.Parse(cloudEvent);
        logger.LogInformation($"Event received: {JsonConvert.SerializeObject(parsedEvent)}");

        var callConnection = callAutomationClient.GetCallConnection(parsedEvent.CallConnectionId);
        var callMedia = callConnection.GetCallMedia();

        if (callConnection == null || callMedia == null)
        {
            return Results.BadRequest($"Call objects failed to get for connection id {parsedEvent.CallConnectionId}.");
        }

        if (parsedEvent is CallConnected)
        {
            // Start recognize prompt - play audio and recognize 1-digit DTMF input
            CallMediaRecognizeDtmfOptions callMediaRecognizeDtmfOptions = new CallMediaRecognizeDtmfOptions(CommunicationIdentifier.FromRawId(callerId), maxTonesToCollect: 1);
            logger.LogInformation($"Created call with correlation id: {parsedEvent.CorrelationId}");
            callMediaRecognizeDtmfOptions.Prompt = new FileSource(new Uri(callbackUriHost + "/audio/MainMenu.wav"));
            callMediaRecognizeDtmfOptions.InterruptPrompt = true;
            callMediaRecognizeDtmfOptions.InitialSilenceTimeout = TimeSpan.FromSeconds(5);
            await callMedia.StartRecognizingAsync(callMediaRecognizeDtmfOptions);
        }
        if (parsedEvent is RecognizeCompleted recognizeCompleted)
        {
            // Play audio once recognition is completed sucessfully
            string selectedTone = ((DtmfResult)recognizeCompleted.RecognizeResult).ConvertToString();

            switch (selectedTone)
            {
                case "1":
                    await callMedia.PlayToAllAsync(new FileSource(new Uri(callbackUriHost + "/audio/Confirmed.wav")));
                    break;

                case "2":
                    await callMedia.PlayToAllAsync(new FileSource(new Uri(callbackUriHost + "/audio/Goodbye.wav")));
                    break;

                default:
                    //invalid tone
                    await callMedia.PlayToAllAsync(new FileSource(new Uri(callbackUriHost + "/audio/Invalid.wav")));
                    break;
            }
        }
        if (parsedEvent is RecognizeFailed recognizeFailedEvent)
        {
            logger.LogInformation($"RecognizeFailed parsedEvent received for call connection id: {parsedEvent.CallConnectionId}");

            // Check for time out, and then play audio message
            if (recognizeFailedEvent.ReasonCode.Equals(MediaEventReasonCode.RecognizeInitialSilenceTimedOut))
            {
                await callMedia.PlayToAllAsync(new FileSource(new Uri(callbackUriHost + "/audio/Timeout.wav")));
            }
        }
        if (parsedEvent is PlayFailed)
        {
            logger.LogInformation($"PlayFailed Event: {JsonConvert.SerializeObject(parsedEvent)}");
            await callConnection.HangUpAsync(true);
        }
    }
    return Results.Ok();
}).Produces(StatusCodes.Status200OK);

//Reject Calls
app.MapPost("/rejectCall", async (
    [FromBody] EventGridEvent[] eventGridEvents,
    ILogger<Program> logger) =>
{
    foreach (var eventGridEvent in eventGridEvents)
    {
        var rejectOption = new RejectCallOptions(incomingCallContext);
        rejectOption.CallRejectReason = CallRejectReason.Forbidden;
        _ = await callAutomationClient.RejectCallAsync(rejectOption);
    }
});

//HangUp Calls
app.MapPost("/hangUp", async (
    [FromBody] EventGridEvent[] eventGridEvents,
    ILogger<Program> logger) =>
{
    foreach (var eventGridEvent in eventGridEvents)
    {
        _ = await callConnection.HangUpAsync(forEveryone: true);
    }
});

//Redirect to PSTN
app.MapPost("/redirect", async (
    [FromBody] EventGridEvent[] eventGridEvents,
    ILogger<Program> logger) =>
{
    foreach (var eventGridEvent in eventGridEvents)
    {
        logger.LogInformation($"CONTEXT IN REDIRECT : {incomingCallContext}");
        var callerNumber = new PhoneNumberIdentifier(acsPhonenumber);
        var target = new CallInvite(new PhoneNumberIdentifier(targetPhonenumber), callerNumber);
        _ = await callAutomationClient.RedirectCallAsync(incomingCallContext, target);
    }
});

//Transfer to PSTN
app.MapPost("/transfer", async (
    [FromBody] EventGridEvent[] eventGridEvents,
    ILogger<Program> logger) =>
{
    foreach (var eventGridEvent in eventGridEvents)
    {

        var transferDestination = new PhoneNumberIdentifier(targetPhonenumber);
        logger.LogInformation($"transferDestination is : {transferDestination}");
        var transferOption = new TransferToParticipantOptions(transferDestination);
        TransferCallToParticipantResult result = await callConnection.TransferCallToParticipantAsync(transferOption);
    }
});

//Add Participant
app.MapPost("/addParticipant", async (
    [FromBody] EventGridEvent[] eventGridEvents,
    ILogger<Program> logger) =>
{
    foreach (var eventGridEvent in eventGridEvents)
    {
        var callerNumber = new PhoneNumberIdentifier(acsPhonenumber);
        var target = new CallInvite(new PhoneNumberIdentifier(ParticipantToAdd), callerNumber);
        _ = await callConnection.AddParticipantAsync(target);
    }
});

app.MapGet("/download", (ILogger<Program> logger) =>
{
    callAutomationClient.GetCallRecording().DownloadTo(new Uri(recordingLocation), "testfile.wav");
    return Results.Ok();
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(Path.Combine(builder.Environment.ContentRootPath, "audio")),
    RequestPath = "/audio"
});

app.Run();
