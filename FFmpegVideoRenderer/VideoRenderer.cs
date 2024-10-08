﻿using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Sdcb.FFmpeg.Codecs;
using Sdcb.FFmpeg.Formats;
using Sdcb.FFmpeg.Raw;
using Sdcb.FFmpeg.Swscales;
using Sdcb.FFmpeg.Toolboxs.Extensions;
using Sdcb.FFmpeg.Utils;
using SkiaSharp;

namespace FFmpegVideoRenderer;

public static class VideoRenderer
{
    static readonly Dictionary<VideoTransition, IVideoTransition> _videoTransitions = new()
    {
        [VideoTransition.Fade] = new FadeTransition(),
        [VideoTransition.SlideX] = new SlideXTransition(),
    };

    static bool HasMoreFrames(Project project, TimeSpan time)
    {
        foreach (var track in project.AudioTracks)
        {
            if (track.Children.Any(item => item.AbsoluteEndTime > time))
                return true;
        }

        foreach (var track in project.VideoTracks)
        {
            if (track.Children.Any(item => item.AbsoluteEndTime > time))
                return true;
        }

        return false;
    }

    static TimeSpan GetMediaSourceRelatedTime(TrackItem trackItem, TimeSpan globalTime)
    {
        return globalTime - trackItem.Offset + trackItem.StartTime;
    }

    static SKRect LayoutVideoTrackItem(Project project, VideoTrackItem videoTrackItem)
    {
        if (videoTrackItem.PositionX is 0 &&
            videoTrackItem.PositionY is 0 &&
            videoTrackItem.SizeWidth is 0 &&
            videoTrackItem.SizeHeight is 0)
        {
            return new SKRect(0, 0, project.OutputWidth, project.OutputHeight);
        }

        return new SKRect(
            videoTrackItem.PositionX, 
            videoTrackItem.PositionY, 
            videoTrackItem.PositionX + videoTrackItem.SizeWidth, 
            videoTrackItem.PositionY + videoTrackItem.SizeHeight);
    }

    public static TimeSpan GetAudioTime(Project project)
    {
        TimeSpan time = TimeSpan.Zero;
        foreach (var track in project.AudioTracks)
        {
            if (track.Children.Count > 0)
            {
                if (track.Children.Count == 0)
                    continue;

                var max = track.Children.Max(item => item.AbsoluteEndTime);
                if (max > time)
                    time = max;
            }
        }

        foreach (var track in project.VideoTracks)
        {
            if (track.Children.Count > 0)
            {
                if (track.Children.Count == 0)
                    continue;

                var max = track.Children.Max(item => item.AbsoluteEndTime);
                if (max > time)
                    time = max;
            }
        }

        return time;
    }
    public static TimeSpan GetVideoTime(Project project)
    {
        TimeSpan time = TimeSpan.Zero;
        foreach (var track in project.VideoTracks)
        {
            if (track.Children.Count == 0)
                continue;

            var max = track.Children.Max(item => item.AbsoluteEndTime);
            if (max > time)
                time = max;
        }

        return time;
    }

    static void CombineAudioSample(
        Dictionary<TrackItem, MediaSource> mediaSources,
        List<TrackItem> bufferTrackItemsToRender,
        AudioTrack track,
        TimeSpan time,
        out float sampleLeft,
        out float sampleRight)
    {
        sampleLeft = 0;
        sampleRight = 0;

        bufferTrackItemsToRender.Clear();
        foreach (var trackItem in track.Children.Where(trackItem => trackItem.IsTimeInRange(time)))
        {
            bufferTrackItemsToRender.Add(trackItem);
        }

        if (bufferTrackItemsToRender.Count == 1)
        {
            var trackItem = bufferTrackItemsToRender[0];

            if (mediaSources.TryGetValue(trackItem, out var mediaSource) &&
                mediaSource.HasAudio)
            {
                var relativeTime = GetMediaSourceRelatedTime(trackItem, time);
                if (mediaSource.GetAudioSample(relativeTime) is AudioSample sample)
                {
                    sampleLeft += sample.LeftValue;
                    sampleRight += sample.RightValue;
                }
            }
        }
        else if (bufferTrackItemsToRender.Count >= 2)
        {
            var trackItem1 = bufferTrackItemsToRender[0];
            var trackItem2 = bufferTrackItemsToRender[1];

            if (mediaSources.TryGetValue(trackItem1, out var mediaSource1) &&
                mediaSources.TryGetValue(trackItem2, out var mediaSource2) &&
                mediaSource1.HasAudio &&
                mediaSource2.HasAudio &&
                TrackItem.GetIntersectionRate(ref trackItem1, ref trackItem2, time, out _, out var rate))
            {
                var relativeTime1 = GetMediaSourceRelatedTime(trackItem1, time);
                var relativeTime2 = GetMediaSourceRelatedTime(trackItem2, time);

                if (mediaSource1.GetAudioSample(relativeTime1) is AudioSample sample1 &&
                    mediaSource2.GetAudioSample(relativeTime2) is AudioSample sample2)
                {
                    sampleLeft += (float)(sample1.LeftValue * (1 - rate));
                    sampleRight += (float)(sample2.RightValue * (1 - rate));

                    sampleLeft += (float)(sample2.LeftValue * rate);
                    sampleRight += (float)(sample2.RightValue * rate);
                }
            }
        }
    }

    static void CombineAudioSample(
        Dictionary<TrackItem, MediaSource> mediaSources,
        List<TrackItem> bufferTrackItemsToRender,
        VideoTrack track,
        TimeSpan time,
        out float sampleLeft,
        out float sampleRight)
    {
        sampleLeft = 0;
        sampleRight = 0;

        bufferTrackItemsToRender.Clear();
        foreach (var trackItem in track.Children.Where(trackItem => trackItem.Volume != 0 && trackItem.IsTimeInRange(time)))
        {
            bufferTrackItemsToRender.Add(trackItem);
        }

        if (bufferTrackItemsToRender.Count == 1)
        {
            var trackItem = bufferTrackItemsToRender[0];

            if (mediaSources.TryGetValue(trackItem, out var mediaSource) &&
                mediaSource.HasAudio)
            {
                var relativeTime = GetMediaSourceRelatedTime(trackItem, time);
                if (mediaSource.GetAudioSample(relativeTime) is AudioSample sample)
                {
                    sampleLeft += sample.LeftValue * trackItem.Volume;
                    sampleRight += sample.RightValue * trackItem.Volume;
                }
            }
        }
        else if (bufferTrackItemsToRender.Count >= 2)
        {
            var trackItem1 = bufferTrackItemsToRender[0];
            var trackItem2 = bufferTrackItemsToRender[1];

            if (mediaSources.TryGetValue(trackItem1, out var mediaSource1) &&
                mediaSources.TryGetValue(trackItem2, out var mediaSource2) &&
                mediaSource1.HasAudio &&
                mediaSource2.HasAudio &&
                TrackItem.GetIntersectionRate(ref trackItem1, ref trackItem2, time, out _, out var rate))
            {
                var relativeTime1 = GetMediaSourceRelatedTime(trackItem1, time);
                var relativeTime2 = GetMediaSourceRelatedTime(trackItem2, time);

                if (mediaSource1.GetAudioSample(relativeTime1) is AudioSample sample1 &&
                    mediaSource2.GetAudioSample(relativeTime2) is AudioSample sample2)
                {
                    sampleLeft += (float)(sample1.LeftValue * trackItem1.Volume * (1 - rate));
                    sampleRight += (float)(sample2.RightValue * trackItem1.Volume * (1 - rate));

                    sampleLeft += (float)(sample2.LeftValue * trackItem2.Volume * rate);
                    sampleRight += (float)(sample2.RightValue * trackItem2.Volume * rate);
                }
            }
        }
    }

    public static unsafe void Render(Project project, Stream outputStream, IProgress<RenderProgress>? progress, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        RenderProgress renderProgress = new RenderProgress();
        TimeSpan videoTotalTime = GetVideoTime(project);
        TimeSpan audioTotalTime = GetAudioTime(project);
        TimeSpan totalTime = Max(videoTotalTime, audioTotalTime); // videoTotalTime + audioTotalTime;
        if(project.VideoTracks.Sum(v => v.Children.Count) != 0) // 没有视频轨道时不需要渲染kkkkkkkkkkk, 时间减半
        {
            totalTime *= 2;
        }
        TimeSpan maxAudioTime = TimeSpan.Zero;
        TimeSpan maxVideoTime = TimeSpan.Zero;
        static TimeSpan Max(TimeSpan left, TimeSpan right) => left > right ? left : right;
        void SetProgress()
        {
            TimeSpan time = maxAudioTime + maxVideoTime;
            var value = Math.Round((time / totalTime) * 100, 2);
            Debug.Assert(value <= 100);
            renderProgress.Progress = value;
            progress?.Report(renderProgress);
        }

        AVRational outputFrameRate = new AVRational(1, 30);
        AVRational outputSampleRate = new AVRational(1, 44100);
        int outputAudioFrameSize = 1024;


        Dictionary<TrackItem, MediaSource> mediaSources = new();

        var resourceMap = project.Resources.ToDictionary(v => v.Id);

        // prepare resources
        foreach (var trackItem in project.VideoTracks.SelectMany(v => v.Children).AsEnumerable<TrackItem>().Concat(project.AudioTracks.SelectMany(v => v.Children)))
        {
            mediaSources[trackItem] = MediaSource.Create(resourceMap[trackItem.ResourceId].StreamFactory(), true);
        }

        // prepare rendering
        using FormatContext formatContext = FormatContext.AllocOutput(formatName: "mp4");
        formatContext.VideoCodec = Codec.CommonEncoders.Libx264;
        formatContext.AudioCodec = Codec.CommonEncoders.AAC;

        using CodecContext videoEncoder = new CodecContext(formatContext.VideoCodec)
        {
            Width = project.OutputWidth,
            Height = project.OutputHeight,
            Framerate = outputFrameRate,
            TimeBase = outputFrameRate,
            PixelFormat = AVPixelFormat.Yuv420p,
            Flags = AV_CODEC_FLAG.GlobalHeader,
        };

        AVChannelLayout avChannelLayout = default;
        ffmpeg.av_channel_layout_default(&avChannelLayout, 2);

        using CodecContext audioEncoder = new CodecContext(formatContext.AudioCodec)
        {
            BitRate = 1270000,
            SampleFormat = AVSampleFormat.Fltp,
            SampleRate = 44100,
            ChLayout = avChannelLayout,
            CodecType = AVMediaType.Audio,
            FrameSize = outputAudioFrameSize,
            TimeBase = new AVRational(1, 44100)
        };

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            formatContext.Dispose();
            throw;
        }

        MediaStream videoStream = formatContext.NewStream(formatContext.VideoCodec);
        MediaStream audioStream = formatContext.NewStream(formatContext.AudioCodec);

        videoEncoder.Open(formatContext.VideoCodec);
        audioEncoder.Open(formatContext.AudioCodec);


        videoStream.Codecpar!.CopyFrom(videoEncoder);
        videoStream.TimeBase = videoEncoder.TimeBase;

        audioStream.Codecpar!.CopyFrom(audioEncoder);
        audioStream.TimeBase = audioEncoder.TimeBase;

        using IOContext ioc = IOContext.WriteStream(outputStream);
        formatContext.Pb = ioc;

        using SKBitmap videoBitmap = new SKBitmap(project.OutputWidth, project.OutputHeight, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using SKCanvas videoCanvas = new SKCanvas(videoBitmap);

        using SKBitmap transitionBitmap = new SKBitmap(project.OutputWidth, project.OutputHeight, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using SKCanvas transitionCanvas = new SKCanvas(transitionBitmap);

        using VideoFrameConverter frameConverter = new VideoFrameConverter();


        // write header
        formatContext.WriteHeader();

        // prepare
        using var packetRef = new Packet();
        List<TrackItem> trackItemsToRender = new();


        float[] leftSampleFrameBuffer = new float[outputAudioFrameSize];
        float[] rightSampleFrameBuffer = new float[outputAudioFrameSize];

        // audio encoding
        long sampleIndex = 0;
        while (true)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                formatContext.Dispose();
                throw;
            }

            var framePts = sampleIndex;
            var frameTime = TimeSpan.FromSeconds((double)sampleIndex * outputSampleRate.Num / outputSampleRate.Den);
            if (!HasMoreFrames(project, frameTime))
            {
                break;
            }

            var frame = new Frame();
            frame.Format = (int)AVSampleFormat.Fltp;
            frame.NbSamples = audioEncoder.FrameSize;
            frame.ChLayout = audioEncoder.ChLayout;
            frame.SampleRate = audioEncoder.SampleRate;

            fixed (float* leftSamplePtr = leftSampleFrameBuffer)
            {
                fixed (float* rightSamplePtr = rightSampleFrameBuffer)
                {
                    // clear the buffer
                    NativeMemory.Clear(leftSamplePtr, (nuint)(sizeof(float) * outputAudioFrameSize));

                    for (int i = 0; i < frame.NbSamples; i++)
                    {
                        var time = TimeSpan.FromSeconds((double)sampleIndex * outputSampleRate.Num / outputSampleRate.Den);
                        maxAudioTime = Max(maxAudioTime, time);
                        SetProgress();

                        if (!HasMoreFrames(project, time))
                        {
                            break;
                        }

                        float sampleLeft = 0;
                        float sampleRight = 0;

                        // audio track
                        foreach (var track in project.AudioTracks)
                        {
                            CombineAudioSample(mediaSources, trackItemsToRender, track, time, out var trackSampleLeft, out var trackSampleRight);
                            sampleLeft += trackSampleLeft;
                            sampleRight += trackSampleRight;
                        }

                        // video track
                        foreach (var track in project.VideoTracks)
                        {
                            if (track.MuteAudio)
                            {
                                continue;
                            }

                            CombineAudioSample(mediaSources, trackItemsToRender, track, time, out var trackSampleLeft, out var trackSampleRight);
                            sampleLeft += trackSampleLeft;
                            sampleRight += trackSampleRight;
                        }

                        leftSamplePtr[i] = sampleLeft;
                        rightSamplePtr[i] = sampleRight;

                        sampleIndex++;
                    }

                    frame.Data[0] = (nint)(void*)(leftSamplePtr);
                    frame.Data[1] = (nint)(void*)(rightSamplePtr);
                    frame.Pts = framePts;

                    foreach (var packet in audioEncoder.EncodeFrame(frame, packetRef))
                    {
                        packet.RescaleTimestamp(audioEncoder.TimeBase, audioStream.TimeBase);
                        packet.StreamIndex = audioStream.Index;

                        formatContext.WritePacket(packet);
                    }
                }
            }
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            formatContext.Dispose();
            return;
        }

        foreach (var packet in audioEncoder.EncodeFrame(null, packetRef))
        {
            packet.RescaleTimestamp(audioEncoder.TimeBase, audioStream.TimeBase);
            packet.StreamIndex = audioStream.Index;


            formatContext.WritePacket(packet);
        }

        // video encoding
        #region Video Encoding
        long frameIndex = 0;
        while (true)
        {
            if (project.VideoTracks.Sum(v => v.Children.Count) == 0)
            {
                break;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                formatContext.Dispose();
                throw;
            }

            var time = TimeSpan.FromSeconds((double)frameIndex * outputFrameRate.Num / outputFrameRate.Den);
            maxVideoTime = Max(maxVideoTime, time);
            SetProgress();
            
            if (!HasMoreFrames(project, time))
            {
                break;
            }

            videoCanvas.Clear();

            // 从下往上绘制
            foreach (var track in project.VideoTracks.Reverse<VideoTrack>())
            {
                trackItemsToRender.Clear();
                foreach (var trackItem in track.Children.Where(trackItem => trackItem.IsTimeInRange(time)))
                {
                    trackItemsToRender.Add(trackItem);
                }

                if (trackItemsToRender.Count == 1)
                {
                    var trackItem = trackItemsToRender[0];
                    if (mediaSources.TryGetValue(trackItem, out var mediaSource))
                    {
                        var frameTime = GetMediaSourceRelatedTime(trackItem, time);
                        if (mediaSource.GetVideoFrameBitmap(frameTime) is SKBitmap frameBitmap)
                        {
                            var dest = LayoutVideoTrackItem(project, (VideoTrackItem)trackItem);

                            RestoreColor(frameBitmap);

                            videoCanvas.DrawBitmap(frameBitmap, dest);
                        }
                    }
                }
                else if (trackItemsToRender.Count >= 2)
                {
                    var trackItem1 = trackItemsToRender[0];
                    var trackItem2 = trackItemsToRender[1];

                    if (mediaSources.TryGetValue(trackItem1, out var mediaSource1) &&
                        mediaSources.TryGetValue(trackItem2, out var mediaSource2) &&
                        mediaSource1.HasVideo &&
                        mediaSource2.HasVideo &&
                        TrackItem.GetIntersectionRate(ref trackItem1, ref trackItem2, time, out var transitionDuration, out var transitionRate))
                    {
                        var relativeTime1 = GetMediaSourceRelatedTime(trackItem1, time);
                        var relativeTime2 = GetMediaSourceRelatedTime(trackItem2, time);

                        if (mediaSource1.GetVideoFrameBitmap(relativeTime1) is SKBitmap frameBitmap1 &&
                            mediaSource2.GetVideoFrameBitmap(relativeTime2) is SKBitmap frameBitmap2)
                        {
                            var dest1 = LayoutVideoTrackItem(project, (VideoTrackItem)trackItem1);
                            var dest2 = LayoutVideoTrackItem(project, (VideoTrackItem)trackItem2);

                            if (_videoTransitions.TryGetValue(((VideoTrackItem)trackItem1).Transition, out var transition))
                            {
                                transitionCanvas.Clear();
                                transition.Render(transitionCanvas, new SKSize(transitionBitmap.Width, transitionBitmap.Height), frameBitmap1, dest1, frameBitmap2, dest2, transitionDuration, (float)transitionRate);

                                RestoreColor(transitionBitmap);
                                videoCanvas.DrawBitmap(transitionBitmap, default(SKPoint));
                            }
                            else
                            {
                                RestoreColor(frameBitmap2);
                                videoCanvas.DrawBitmap(frameBitmap2, dest2);
                            }
                        }

                        // ignore other track items
                        //for (int j = 2; j < trackItemsToRender.Count; j++)
                        //{
                        //    var trackItemOther = trackItemsToRender[j];
                        //    var relativeTimeOther = GetMediaSourceRelatedTime(trackItemOther, time);

                        //    if (_mediaSources.TryGetValue(trackItemOther.ResourceId, out var mediaSourceOther) &&
                        //        mediaSourceOther.GetVideoFrameBitmap(relativeTimeOther) is SKBitmap frameBitmapOther)
                        //    {

                        //    }
                        //}
                    }
                }
            }


            using var frame = new Frame();
            frame.Width = project.OutputWidth;
            frame.Height = project.OutputHeight;
            frame.Format = (int)AVPixelFormat.Bgra;
            frame.Data[0] = videoBitmap.GetPixels();
            frame.Linesize[0] = videoBitmap.RowBytes;
            frame.Pts = frameIndex;

            using var convertedFrame = videoEncoder.CreateFrame();
            convertedFrame.MakeWritable();
            frameConverter.ConvertFrame(frame, convertedFrame);
            convertedFrame.Pts = frameIndex;

            foreach (var packet in videoEncoder.EncodeFrame(convertedFrame, packetRef))
            {
                packet.RescaleTimestamp(videoEncoder.TimeBase, videoStream.TimeBase);
                packet.StreamIndex = videoStream.Index;


                formatContext.WritePacket(packet);
            }

            frameIndex++;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            formatContext.Dispose();
            throw;
        }

        foreach (var packet in videoEncoder.EncodeFrame(null, packetRef))
        {
            packet.RescaleTimestamp(videoEncoder.TimeBase, videoStream.TimeBase);
            packet.StreamIndex = videoStream.Index;

            formatContext.WritePacket(packet);
        }

        #endregion


        formatContext.WriteTrailer();

        renderProgress.Progress = 100;
        progress?.Report(renderProgress);
        outputStream.Flush();
    }

    private static unsafe void RestoreColor(SKBitmap bitmap)
    {
        // TODO: 颜色转换错误
        return;
        var standard = Matrix4x4.Multiply(new Matrix4x4(
                                                      0xFF, 0x00, 0x00, 0x00,
                                                      0x18, 0xD8, 0x0E, 0x00,
                                                      0x00, 0x00, 0xFF, 0x00,
                                                      0x00, 0x00, 0x00, 1), 1 / 255f);
        Matrix4x4.Invert(standard, out var fin);

        if (bitmap.ColorType == SKColorType.Rgba8888)
        {
            var ptr = bitmap.GetPixels();
            var pixels = new Span<(byte R, byte G, byte B, byte A)>((void*)ptr, bitmap.Width * bitmap.Height);
            foreach (ref var item in pixels)
            {
                Matrix4x4 b = new(item.R, 0, 0, 0,
                                    item.G, 0, 0, 0,
                                    item.B, 0, 0, 0,
                                    0x00, 0, 0, 0);
                var result = fin * b;
                item.R = (byte)Math.Round(result.M11);
                item.G = (byte)Math.Round(result.M21);
                item.B = (byte)Math.Round(result.M31);
            }
            return;
        }
        else if(bitmap.ColorType == SKColorType.Bgra8888)
        {
            var ptr = bitmap.GetPixels();
            var pixels = new Span<(byte B, byte G, byte R, byte A)>((void*)ptr, bitmap.Width * bitmap.Height);
            foreach (ref var item in pixels)
            {
                Matrix4x4 b = new(item.R, 0, 0, 0,
                                    item.G, 0, 0, 0,
                                    item.B, 0, 0, 0,
                                    0x00, 0, 0, 0);
                var result = fin * b;
                item.R = (byte)Math.Round(result.M11);
                item.G = (byte)Math.Round(result.M21);
                item.B = (byte)Math.Round(result.M31);
            }
            return;
        }
        throw new NotSupportedException();
    }
}
