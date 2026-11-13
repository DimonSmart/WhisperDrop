using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Collections.Specialized;
using System.IO;
using System.Threading.Tasks;
using WhisperDrop.Media;
using WhisperDrop.Models;
using WhisperDrop.State;

namespace WhisperDrop.Dictation;

/// <summary>
/// UI-thread coordinator. Native hooks never access application state directly.
/// Recognition is scheduled by InitialApplicationState's existing queue/runtime.
/// </summary>
public sealed class WindowsDictationCoordinator : IDisposable
{
    private readonly InitialApplicationState state;
    private readonly IMicrophoneCaptureService microphone;
    private readonly WindowsDictationPlatform platform;
    private readonly Dictionary<TranscriptionQueueItem, WindowsTextInputTarget?> targets = new();
    private WindowsTextInputTarget? recordingTarget;
    private DateTimeOffset recordingStarted;
    private bool starting, recording, stopping, releaseRequested, disposed;

    public WindowsDictationCoordinator(InitialApplicationState state, IMicrophoneCaptureService microphone,
        Action<Action> dispatch, Action showWindow, Action exit)
    {
        this.state = state;
        this.microphone = microphone;
        platform = new WindowsDictationPlatform(dispatch, OnPressed, OnReleased,
            showWindow, () => state.DictationEnabled = !state.DictationEnabled,
            exit, state.SetDictationStatus);
        state.PropertyChanged += OnSettingsChanged;
        state.QueueRows.CollectionChanged += OnQueueChanged;
        state.DictationTranscribed += OnTranscribed;
        ApplySettings();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(InitialApplicationState.DictationEnabled) or
            nameof(InitialApplicationState.DictationHotkey))
            ApplySettings();
    }

    private void ApplySettings()
    {
        var success = platform.Configure(state.DictationHotkey, state.DictationEnabled);
        state.SetDictationStatus(success
            ? state.DictationEnabled ? "Ready" : "Dictation disabled"
            : platform.RegistrationError);
    }

    private async void OnPressed()
    {
        if (disposed || starting || recording || stopping || !state.DictationEnabled) return;
        if (!state.CanStartDictation)
        {
            state.SetDictationStatus("Microphone is busy with another recording.");
            return;
        }

        starting = true;
        releaseRequested = false;
        recordingStarted = DateTimeOffset.Now;
        recordingTarget = state.DictationInsertAfterRecognition ? WindowsTextInputTarget.Capture() : null;

        try
        {
            await microphone.StartAsync();
            if (disposed) return;
            recording = true;
            state.SetDictationRecording(true);
            platform.SetRecordingOverlay(true);
            state.SetDictationStatus("Recording…");
            if (releaseRequested)
                await StopRecordingAsync();
        }
        catch (Exception exception)
        {
            recordingTarget?.Dispose();
            recordingTarget = null;
            if (!disposed) state.SetDictationStatus("Dictation failed: " + exception.Message);
        }
        finally { starting = false; }
    }

    private void OnReleased()
    {
        if (disposed) return;
        releaseRequested = true;
        if (recording && !stopping)
            _ = StopRecordingAsync();
    }

    private async Task StopRecordingAsync()
    {
        if (stopping || !recording) return;
        stopping = true;
        recording = false;
        platform.SetRecordingOverlay(false); // Hide as soon as recording has ended.
        var finished = DateTimeOffset.Now;
        var target = recordingTarget;
        recordingTarget = null;

        try
        {
            var path = await microphone.StopAsync();
            if (disposed)
            {
                target?.Dispose();
                return;
            }

            // An accidental tap without useful audio never creates an empty queue row.
            if (finished - recordingStarted < TimeSpan.FromMilliseconds(250))
            {
                target?.Dispose();
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                state.SetDictationStatus("Ready");
                return;
            }

            var row = state.AddDictationRecording(path, recordingStarted, finished);
            targets.Add(row, target);
            state.SetDictationStatus("Transcribing…");
            state.QueueDictationRecognition(row);
        }
        catch (Exception exception)
        {
            target?.Dispose();
            if (!disposed) state.SetDictationStatus("Dictation failed: " + exception.Message);
        }
        finally
        {
            if (!disposed) state.SetDictationRecording(false);
            stopping = false;
        }
    }

    private void OnQueueChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        foreach (var row in new List<TranscriptionQueueItem>(targets.Keys))
        {
            if (state.QueueRows.Contains(row)) continue;
            if (targets.Remove(row, out var target)) target?.Dispose();
        }
    }

    private void OnTranscribed(TranscriptionQueueItem row)
    {
        if (!targets.Remove(row, out var target)) return;
        using (target)
        {
            if (row.InsertionStatus == TextInsertionStatus.NotRequested)
            {
                state.SetDictationStatus("Text ready");
                return;
            }

            if (row.TranscriptionState != TranscriptionState.Completed)
            {
                row.InsertionStatus = TextInsertionStatus.Skipped;
                row.InsertionError = "Recognition did not complete.";
                state.SetDictationStatus("Dictation failed");
                platform.ShowNotification("Recognition failed. The audio is available in history for retry.");
                return;
            }

            if (string.IsNullOrWhiteSpace(row.RawTranscript))
            {
                row.InsertionStatus = TextInsertionStatus.Skipped;
                row.InsertionError = "No speech was recognized.";
                state.SetDictationStatus("No speech recognized");
                return;
            }

            if (target is null || !state.QueueRows.Contains(row))
            {
                row.InsertionStatus = TextInsertionStatus.Skipped;
                row.InsertionError = "The original text field could not be identified.";
                state.SetDictationStatus("Text not inserted — copy from history");
                platform.ShowNotification("Text not inserted. Open ParakeetDrop to copy it from history.");
                return;
            }

            try
            {
                row.InsertionStatus = target.Insert(row.RawTranscript);
                if (row.InsertionStatus != TextInsertionStatus.Submitted)
                    row.InsertionError = "The original field changed or rejected input.";
                state.SetDictationStatus(row.InsertionStatus == TextInsertionStatus.Submitted
                    ? "Text submitted — check the target field"
                    : "Text not inserted — copy from history");
                if (row.InsertionStatus != TextInsertionStatus.Submitted)
                    platform.ShowNotification("Text not inserted. Open ParakeetDrop to copy it from history.");
            }
            catch (Exception exception)
            {
                row.InsertionStatus = TextInsertionStatus.Failed;
                row.InsertionError = exception.Message;
                state.SetDictationStatus("Text not inserted — copy from history");
                platform.ShowNotification("Text not inserted. Open ParakeetDrop to copy it from history.");
            }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        state.PropertyChanged -= OnSettingsChanged;
        state.QueueRows.CollectionChanged -= OnQueueChanged;
        state.DictationTranscribed -= OnTranscribed;
        platform.SetRecordingOverlay(false);
        platform.Dispose();
        recordingTarget?.Dispose();
        recordingTarget = null;
        foreach (var target in targets.Values)
            target?.Dispose();
        targets.Clear();
        state.CancelTranscription();
    }
}
