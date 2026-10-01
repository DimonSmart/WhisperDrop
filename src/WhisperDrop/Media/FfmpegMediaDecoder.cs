using System;
using System.Buffers;
using System.IO;
using System.Threading;
using FFmpeg.AutoGen.Abstractions;

namespace WhisperDrop.Media;

internal sealed unsafe class FfmpegMediaDecoder
{
    private const int OutputSampleRate = 16000;
    private const int BytesPerOutputSample = 2;

    public TimeSpan? DecodeToWave(
        string sourcePath,
        string outputPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        AVFormatContext* formatContext = null;
        AVCodecContext* codecContext = null;
        AVPacket* packet = null;
        AVFrame* frame = null;
        SwrContext* resampler = null;

        try
        {
            var openResult = ffmpeg.avformat_open_input(&formatContext, sourcePath, null, null);
            if (openResult < 0 || formatContext is null)
                throw Error(MediaPreparationErrorKind.CannotOpenFile, "avformat_open_input", openResult);

            var streamInfoResult = ffmpeg.avformat_find_stream_info(formatContext, null);
            if (streamInfoResult < 0)
                throw Error(MediaPreparationErrorKind.CorruptedOrIncomplete, "avformat_find_stream_info", streamInfoResult);

            AVCodec* decoder = null;
            var streamIndex = ffmpeg.av_find_best_stream(
                formatContext,
                AVMediaType.AVMEDIA_TYPE_AUDIO,
                -1,
                -1,
                &decoder,
                0);

            if (streamIndex == ffmpeg.AVERROR_STREAM_NOT_FOUND)
                throw MediaPreparationException.Create(MediaPreparationErrorKind.NoAudioStream);
            if (streamIndex == ffmpeg.AVERROR_DECODER_NOT_FOUND || decoder is null)
                throw MediaPreparationException.Create(MediaPreparationErrorKind.UnsupportedAudioCodec);
            if (streamIndex < 0)
                throw Error(MediaPreparationErrorKind.DecoderFailure, "av_find_best_stream", streamIndex);

            var stream = formatContext->streams[streamIndex];
            codecContext = ffmpeg.avcodec_alloc_context3(decoder);
            if (codecContext is null)
            {
                throw MediaPreparationException.Create(
                    MediaPreparationErrorKind.DecoderFailure,
                    new OutOfMemoryException("avcodec_alloc_context3 returned null."));
            }

            CheckDecoder(ffmpeg.avcodec_parameters_to_context(codecContext, stream->codecpar), "avcodec_parameters_to_context");
            var codecOpenResult = ffmpeg.avcodec_open2(codecContext, decoder, null);
            if (codecOpenResult < 0)
                throw Error(MediaPreparationErrorKind.UnsupportedAudioCodec, "avcodec_open2", codecOpenResult);

            packet = ffmpeg.av_packet_alloc();
            frame = ffmpeg.av_frame_alloc();
            if (packet is null || frame is null)
            {
                throw MediaPreparationException.Create(
                    MediaPreparationErrorKind.DecoderFailure,
                    new OutOfMemoryException("FFmpeg packet/frame allocation failed."));
            }

            var duration = GetDuration(formatContext, stream);
            progress?.Report(0);

            using var writer = new PcmWaveWriter(outputPath);
            int readResult;
            while ((readResult = ffmpeg.av_read_frame(formatContext, packet)) >= 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (packet->stream_index != streamIndex)
                        continue;

                    var sendResult = ffmpeg.avcodec_send_packet(codecContext, packet);
                    if (sendResult < 0)
                        throw Error(MediaPreparationErrorKind.CorruptedOrIncomplete, "avcodec_send_packet", sendResult);

                    DrainDecoder(codecContext, frame, ref resampler, writer, stream, duration, progress, cancellationToken);
                }
                finally
                {
                    ffmpeg.av_packet_unref(packet);
                }
            }

            if (readResult != ffmpeg.AVERROR_EOF)
                throw Error(MediaPreparationErrorKind.CorruptedOrIncomplete, "av_read_frame", readResult);

            var flushResult = ffmpeg.avcodec_send_packet(codecContext, null);
            if (flushResult < 0 && flushResult != ffmpeg.AVERROR_EOF)
                throw Error(MediaPreparationErrorKind.DecoderFailure, "avcodec_send_packet(flush)", flushResult);

            DrainDecoder(codecContext, frame, ref resampler, writer, stream, duration, progress, cancellationToken);
            FlushResampler(resampler, writer, cancellationToken);
            writer.Complete();
            progress?.Report(1);
            return duration;
        }
        catch (MediaPreparationException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw MediaPreparationException.Create(MediaPreparationErrorKind.DecoderFailure, exception);
        }
        finally
        {
            if (resampler is not null)
                ffmpeg.swr_free(&resampler);
            if (frame is not null)
                ffmpeg.av_frame_free(&frame);
            if (packet is not null)
                ffmpeg.av_packet_free(&packet);
            if (codecContext is not null)
                ffmpeg.avcodec_free_context(&codecContext);
            if (formatContext is not null)
                ffmpeg.avformat_close_input(&formatContext);
        }
    }

    private static void DrainDecoder(
        AVCodecContext* codecContext,
        AVFrame* frame,
        ref SwrContext* resampler,
        PcmWaveWriter writer,
        AVStream* stream,
        TimeSpan? duration,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var receiveResult = ffmpeg.avcodec_receive_frame(codecContext, frame);
            if (receiveResult == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receiveResult == ffmpeg.AVERROR_EOF)
                return;
            if (receiveResult < 0)
                throw Error(MediaPreparationErrorKind.CorruptedOrIncomplete, "avcodec_receive_frame", receiveResult);

            try
            {
                if (resampler is null)
                    resampler = CreateResampler(codecContext, frame);

                WriteConvertedFrame(resampler, frame, writer, cancellationToken);
                ReportProgress(frame, stream, duration, progress);
            }
            finally
            {
                ffmpeg.av_frame_unref(frame);
            }
        }
    }

    private static SwrContext* CreateResampler(AVCodecContext* codecContext, AVFrame* frame)
    {
        var inputRate = frame->sample_rate > 0 ? frame->sample_rate : codecContext->sample_rate;
        if (inputRate <= 0)
            throw MediaPreparationException.Create(MediaPreparationErrorKind.DecoderFailure);

        var inputLayout = frame->ch_layout.nb_channels > 0 ? frame->ch_layout : codecContext->ch_layout;
        if (inputLayout.nb_channels <= 0)
            throw MediaPreparationException.Create(MediaPreparationErrorKind.DecoderFailure);

        var outputLayout = new AVChannelLayout();
        ffmpeg.av_channel_layout_default(&outputLayout, 1);
        SwrContext* result = null;
        try
        {
            var configureResult = ffmpeg.swr_alloc_set_opts2(
                &result,
                &outputLayout,
                AVSampleFormat.AV_SAMPLE_FMT_S16,
                OutputSampleRate,
                &inputLayout,
                (AVSampleFormat)frame->format,
                inputRate,
                0,
                null);
            if (configureResult < 0 || result is null)
                throw Error(MediaPreparationErrorKind.DecoderFailure, "swr_alloc_set_opts2", configureResult);

            var initResult = ffmpeg.swr_init(result);
            if (initResult < 0)
            {
                ffmpeg.swr_free(&result);
                throw Error(MediaPreparationErrorKind.DecoderFailure, "swr_init", initResult);
            }

            return result;
        }
        finally
        {
            ffmpeg.av_channel_layout_uninit(&outputLayout);
        }
    }

    private static void WriteConvertedFrame(
        SwrContext* resampler,
        AVFrame* frame,
        PcmWaveWriter writer,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var outputSamples = ffmpeg.swr_get_out_samples(resampler, frame->nb_samples);
        if (outputSamples <= 0)
            return;

        var buffer = ArrayPool<byte>.Shared.Rent(checked(outputSamples * BytesPerOutputSample));
        try
        {
            int converted;
            fixed (byte* output = buffer)
            {
                byte** outputPlanes = stackalloc byte*[1];
                outputPlanes[0] = output;
                converted = ffmpeg.swr_convert(
                    resampler,
                    outputPlanes,
                    outputSamples,
                    frame->extended_data,
                    frame->nb_samples);
            }

            if (converted < 0)
                throw Error(MediaPreparationErrorKind.DecoderFailure, "swr_convert", converted);

            cancellationToken.ThrowIfCancellationRequested();
            writer.Write(buffer.AsSpan(0, checked(converted * BytesPerOutputSample)));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void FlushResampler(SwrContext* resampler, PcmWaveWriter writer, CancellationToken cancellationToken)
    {
        if (resampler is null)
            return;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outputSamples = ffmpeg.swr_get_out_samples(resampler, 0);
            if (outputSamples <= 0)
                return;

            var buffer = ArrayPool<byte>.Shared.Rent(checked(outputSamples * BytesPerOutputSample));
            try
            {
                int converted;
                fixed (byte* output = buffer)
                {
                    byte** outputPlanes = stackalloc byte*[1];
                    outputPlanes[0] = output;
                    converted = ffmpeg.swr_convert(resampler, outputPlanes, outputSamples, null, 0);
                }

                if (converted < 0)
                    throw Error(MediaPreparationErrorKind.DecoderFailure, "swr_convert(flush)", converted);
                if (converted == 0)
                    return;

                writer.Write(buffer.AsSpan(0, checked(converted * BytesPerOutputSample)));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private static TimeSpan? GetDuration(AVFormatContext* formatContext, AVStream* stream)
    {
        if (stream->duration > 0 && stream->duration != ffmpeg.AV_NOPTS_VALUE)
        {
            var seconds = stream->duration * ffmpeg.av_q2d(stream->time_base);
            if (seconds > 0 && double.IsFinite(seconds))
                return TimeSpan.FromSeconds(seconds);
        }

        if (formatContext->duration > 0 && formatContext->duration != ffmpeg.AV_NOPTS_VALUE)
        {
            var seconds = (double)formatContext->duration / ffmpeg.AV_TIME_BASE;
            if (seconds > 0 && double.IsFinite(seconds))
                return TimeSpan.FromSeconds(seconds);
        }

        return null;
    }

    private static void ReportProgress(AVFrame* frame, AVStream* stream, TimeSpan? duration, IProgress<double>? progress)
    {
        if (progress is null || duration is not { } knownDuration || knownDuration <= TimeSpan.Zero)
            return;

        var timestamp = frame->best_effort_timestamp;
        if (timestamp == ffmpeg.AV_NOPTS_VALUE)
            return;

        var positionSeconds = timestamp * ffmpeg.av_q2d(stream->time_base);
        progress.Report(Math.Clamp(positionSeconds / knownDuration.TotalSeconds, 0, 0.999));
    }

    private static void CheckDecoder(int result, string operation)
    {
        if (result < 0)
            throw Error(MediaPreparationErrorKind.DecoderFailure, operation, result);
    }

    private static MediaPreparationException Error(MediaPreparationErrorKind kind, string operation, int errorCode)
    {
        var buffer = stackalloc byte[1024];
        var text = ffmpeg.av_strerror(errorCode, buffer, 1024) >= 0
            ? System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)buffer)
            : null;
        var diagnostic = string.IsNullOrWhiteSpace(text)
            ? $"{operation} failed with FFmpeg error {errorCode}."
            : $"{operation} failed with FFmpeg error {errorCode}: {text}";
        return MediaPreparationException.Create(kind, new InvalidDataException(diagnostic));
    }
}
