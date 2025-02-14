using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpegVideoRenderer.Values;
using Microsoft.Extensions.Logging;
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
    public static ILogger? Logger { get; set; }

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
        AudioTrackLine track,
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
            var trackItem = (AudioTrackItem)bufferTrackItemsToRender[0];

            if (mediaSources.TryGetValue(trackItem, out var mediaSource) &&
                mediaSource.HasAudio)
            {
                var relativeTime = GetMediaSourceRelatedTime(trackItem, time);
                var opacity = trackItem.SoundKeyFrames.Sample(relativeTime);

                if (mediaSource.GetAudioSample(relativeTime) is AudioSample sample)
                {
                    sampleLeft += (float)(sample.LeftValue * opacity.Value * trackItem.Volume);
                    sampleRight += (float)(sample.RightValue * opacity.Value * trackItem.Volume);
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
        VideoTrackLine track,
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

    public static async Task Render(Project project, Stream outputStream, IProgress<RenderProgress>? progress, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        Logger?.LogInformation("[Render] 导出视频开始 {Name}", project.Name);
        cancellationToken.ThrowIfCancellationRequested();

        RenderProgress renderProgress = new RenderProgress();
        TimeSpan videoTotalTime = GetVideoTime(project);
        TimeSpan audioTotalTime = GetAudioTime(project);
        TimeSpan totalTime = Max(videoTotalTime, audioTotalTime); // videoTotalTime + audioTotalTime;
        if (project.VideoTracks.Sum(v => v.Children.Count) != 0) // 没有视频轨道时不需要渲染, 时间减半
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
        const int OutputAudioFrameSize = 1024;

        Dictionary<TrackItem, MediaSource> mediaSources = new();

        var resourceMap = project.Resources.ToDictionary(v => v.Id);

        var audioTracksToRender = new List<AudioTrackLine>();
        audioTracksToRender.AddRange(project.AudioTracks);

        // prepare resources
        foreach (var trackLine in project.VideoTracks)
        {
            var videoAudioTrackLine = new AudioTrackLine(); // 专门存放视频的音频的音频轨道

            foreach (var trackItem in trackLine.Children)
            {
                var stream = resourceMap[trackItem.ResourceId].StreamFactory();

                if (trackItem is VideoTrackItem videoTrackItem) // 单独把视频里面的音频抽离出来
                {
                    var audioTrackItem = videoTrackItem.ToAudioTrackItem();
                    videoAudioTrackLine.Children.Add(audioTrackItem);
                    var aStream = new MemoryStream();
                    if (await ToAudioStream(stream, aStream))
                    {
                        aStream.Position = 0;
                        mediaSources[audioTrackItem] = MediaSource.Create(aStream, true);
                    }
                    stream.Position = 0;
                }

                mediaSources[trackItem] = MediaSource.Create(stream, true);
            }

            audioTracksToRender.Add(videoAudioTrackLine);
        }

        var mediaSourcesOnlyAudio = mediaSources.ToDictionary();
        foreach (var item in project.AudioTracks.SelectMany(v => v.Children))
        {
            mediaSourcesOnlyAudio[item] = MediaSource.Create(resourceMap[item.ResourceId].StreamFactory());
        }
        foreach (var item in mediaSources.Keys.OfType<VideoTrackItem>())
        {
            mediaSourcesOnlyAudio.Remove(item);
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
        unsafe
        {
            ffmpeg.av_channel_layout_default(&avChannelLayout, 2);
        }

        using CodecContext audioEncoder = new CodecContext(formatContext.AudioCodec)
        {
            BitRate = 1270000,
            SampleFormat = AVSampleFormat.Fltp,
            SampleRate = 44100,
            ChLayout = avChannelLayout,
            CodecType = AVMediaType.Audio,
            FrameSize = OutputAudioFrameSize,
            TimeBase = new AVRational(1, 44100)
        };
        Logger?.LogInformation("[Render] 编解码器创建完成 {Name}", project.Name);

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

        Span<float> leftSampleFrameBuffer = stackalloc float[OutputAudioFrameSize];
        Span<float> rightSampleFrameBuffer = stackalloc float[OutputAudioFrameSize];

        Logger?.LogInformation("[Render] 开始编解码音频 {Name}", project.Name);
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

            // clear the buffer
            leftSampleFrameBuffer.Clear();

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
                foreach (var trackLine in audioTracksToRender)
                {
                    CombineAudioSample(mediaSourcesOnlyAudio, trackItemsToRender, trackLine, time, out var trackSampleLeft, out var trackSampleRight);
                    sampleLeft += trackSampleLeft;
                    sampleRight += trackSampleRight;
                }

                // video track // 不再编解码视频中的音频
                //foreach (var track in project.VideoTracks)
                //{
                //    if (track.MuteAudio)
                //    {
                //        continue;
                //    }

                //    CombineAudioSample(mediaSources, trackItemsToRender, track, time, out var trackSampleLeft, out var trackSampleRight);
                //    sampleLeft += trackSampleLeft;
                //    sampleRight += trackSampleRight;
                //}

                leftSampleFrameBuffer[i] = sampleLeft;
                rightSampleFrameBuffer[i] = sampleRight;

                sampleIndex++;
            }

            unsafe
            {
                frame.Data[0] = (nint)Unsafe.AsPointer(ref leftSampleFrameBuffer[0]);
                frame.Data[1] = (nint)Unsafe.AsPointer(ref rightSampleFrameBuffer[0]);
            }
            frame.Pts = framePts;

            foreach (var packet in audioEncoder.EncodeFrame(frame, packetRef))
            {
                packet.RescaleTimestamp(audioEncoder.TimeBase, audioStream.TimeBase);
                packet.StreamIndex = audioStream.Index;

                formatContext.WritePacket(packet);
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

        Logger?.LogInformation("[Render] 开始编解码视频 {Name}", project.Name);
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

            // 从下往上绘制, 顶部的覆盖底部的
            foreach (var trackLine in project.VideoTracks.Reverse<VideoTrackLine>())
            {
                trackItemsToRender.Clear();
                foreach (var trackItem in trackLine.Children.Where(trackItem => trackItem.IsTimeInRange(time)))
                {
                    trackItemsToRender.Add(trackItem);
                }

                if (trackItemsToRender.Count == 1)
                {
                    var trackItem = (VideoTrackItem)trackItemsToRender[0];
                    if (mediaSources.TryGetValue(trackItem, out var mediaSource))
                    {
                        var frameTime = GetMediaSourceRelatedTime(trackItem, time);
                        var opacity = trackItem.OpacityKeyFrames.Sample(frameTime);
                        var translate = trackItem.TranslateKeyFrames.Sample(frameTime);
                        var scale = trackItem.ScaleKeyFrames.Sample(frameTime);

                        if (mediaSource.GetVideoFrameBitmap(frameTime) is SKBitmap frameBitmap)
                        {
                            var dest = LayoutVideoTrackItem(project, trackItem);

                            dest.Left += (int)translate.OffsetX;
                            dest.Right += (int)translate.OffsetX;
                            dest.Top += (int)translate.OffsetY;
                            dest.Bottom += (int)translate.OffsetY;

                            var newWidth = dest.Width * scale.ScaleX;
                            var newHeight = dest.Height * scale.ScaleX;
                            var widthDiff = newWidth - dest.Width;
                            var heightDiff = newHeight - dest.Height;

                            dest.Left -= (int)(widthDiff / 2);
                            dest.Top -= (int)(heightDiff / 2);
                            dest.Right += (int)(widthDiff / 2);
                            dest.Bottom += (int)(heightDiff / 2);

                            MultiplyAlpha(frameBitmap, (float)opacity.Value);

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

                        var opacity1 = ((VideoTrackItem)trackItem1).OpacityKeyFrames.Sample(relativeTime1);
                        var translate1 = ((VideoTrackItem)trackItem1).TranslateKeyFrames.Sample(relativeTime1);
                        var scale1 = ((VideoTrackItem)trackItem1).ScaleKeyFrames.Sample(relativeTime1);

                        var opacity2 = ((VideoTrackItem)trackItem2).OpacityKeyFrames.Sample(relativeTime2);
                        var translate2 = ((VideoTrackItem)trackItem2).TranslateKeyFrames.Sample(relativeTime2);
                        var scale2 = ((VideoTrackItem)trackItem2).ScaleKeyFrames.Sample(relativeTime2);

                        if (mediaSource1.GetVideoFrameBitmap(relativeTime1) is SKBitmap frameBitmap1 &&
                            mediaSource2.GetVideoFrameBitmap(relativeTime2) is SKBitmap frameBitmap2)
                        {
                            var dest1 = LayoutVideoTrackItem(project, (VideoTrackItem)trackItem1);
                            var dest2 = LayoutVideoTrackItem(project, (VideoTrackItem)trackItem2);

                            dest1.Left += (int)translate1.OffsetX;
                            dest1.Right += (int)translate1.OffsetX;
                            dest1.Top += (int)translate1.OffsetY;
                            dest1.Bottom += (int)translate1.OffsetY;

                            var newWidth = dest1.Width * scale1.ScaleX;
                            var newHeight = dest1.Height * scale1.ScaleX;
                            var widthDiff = newWidth - dest1.Width;
                            var heightDiff = newHeight - dest1.Height;

                            dest1.Left -= (int)(widthDiff / 2);
                            dest1.Top -= (int)(heightDiff / 2);
                            dest1.Right += (int)(widthDiff / 2);
                            dest1.Bottom += (int)(heightDiff / 2);

                            MultiplyAlpha(frameBitmap1, (float)opacity1.Value);

                            dest2.Left += (int)translate2.OffsetX;
                            dest2.Right += (int)translate2.OffsetX;
                            dest2.Top += (int)translate2.OffsetY;
                            dest2.Bottom += (int)translate2.OffsetY;

                            newWidth = dest2.Width * scale2.ScaleX;
                            newHeight = dest2.Height * scale2.ScaleX;
                            widthDiff = newWidth - dest2.Width;
                            heightDiff = newHeight - dest2.Height;

                            dest2.Left -= (int)(widthDiff / 2);
                            dest2.Top -= (int)(heightDiff / 2);
                            dest2.Right += (int)(widthDiff / 2);
                            dest2.Bottom += (int)(heightDiff / 2);


                            if (trackItem1.Transition.HasValue && _videoTransitions.TryGetValue(trackItem1.Transition.Value, out var transition))
                            {
                                transitionCanvas.Clear();
                                transition.Render(transitionCanvas, new SKSize(transitionBitmap.Width, transitionBitmap.Height), frameBitmap1, dest1, frameBitmap2, dest2, transitionDuration, (float)transitionRate);

                                videoCanvas.DrawBitmap(transitionBitmap, default(SKPoint));
                            }
                            else
                            {
                                using SKPaint paint = new();

                                paint.Color = new SKColor(0,0,0, (byte)(opacity1.Value * 255));
                                videoCanvas.DrawBitmap(frameBitmap1, dest1, paint);
                                paint.Color = new SKColor(0,0,0, (byte)(opacity2.Value * 255));
                                videoCanvas.DrawBitmap(frameBitmap2, dest2, paint);
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
        Logger?.LogInformation("[Render] End {Name}", project.Name);

        formatContext.WriteTrailer();

        renderProgress.Progress = 100;
        progress?.Report(renderProgress);
        outputStream.Flush();
    }

    private static unsafe void MultiplyAlpha(SKBitmap bitmap, float opacity)
    {
        if (bitmap.ColorType is SKColorType.Rgba8888 or SKColorType.Bgra8888)
        {
            var ptr = bitmap.GetPixels();
            var pixels = new Span<ColorXxxa>((void*)ptr, bitmap.Width * bitmap.Height);
            var size = Vector<float>.Count;
            Span<float> buffer = stackalloc float[size];
            for (int i = 0; i <= pixels.Length - size; i += size)
            {
                for (int j = 0; j < size; j++)
                {
                    buffer[j] = pixels[i + j].A;
                }
                var newVec = new Vector<float>(buffer) * opacity;
                for (int j = 0; j < size; j++)
                {
                    pixels[i + j].A = (byte)newVec[j];
                }
            }
            foreach (ref var v in pixels[..^(pixels.Length % size)])
            {
                v.A = (byte)(opacity * v.A);
            }
        }
    }

    private static unsafe void FixColor(SKBitmap bitmap)
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
        else if (bitmap.ColorType == SKColorType.Bgra8888)
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

    /// <summary>
    /// 抽离视频中的音频
    /// </summary>
    /// <param name="videoStream"></param>
    /// <param name="outputStream"></param>
    /// <returns>如果返回false表示该视频没有音频轨道</returns>
    public static async Task<bool> ToAudioStream(Stream videoStream, Stream outputStream)
    {
        using var inFc = FormatContext.OpenInputIO(IOContext.ReadStream(videoStream));
        if (inFc.FindBestStreamOrNull(AVMediaType.Audio) is null)
        {
            return false;
        }
        var inAudioStream = inFc.GetAudioStream();
        using CodecContext audioDecoder = new(Codec.FindDecoderById(inAudioStream.Codecpar!.CodecId));
        audioDecoder.FillParameters(inAudioStream.Codecpar);
        audioDecoder.Open();
        audioDecoder.ChLayout = audioDecoder.ChLayout;
        using var outFc = FormatContext.AllocOutput(formatName: "mp3");
        outFc.AudioCodec = Codec.CommonEncoders.Libmp3lame;
        var outAudioStream = outFc.NewStream(outFc.AudioCodec);
        using var audioEncoder = new CodecContext(outFc.AudioCodec)
        {
            ChLayout = audioDecoder.ChLayout,
            SampleFormat = outFc.AudioCodec.Value.NegociateSampleFormat(AVSampleFormat.Fltp),
            SampleRate = outFc.AudioCodec.Value.NegociateSampleRates(inAudioStream.Codecpar.SampleRate),
            BitRate = inAudioStream.Codecpar.BitRate
        };
        audioEncoder.ChLayout = audioEncoder.ChLayout;
        audioEncoder.TimeBase = new AVRational(1, audioEncoder.SampleRate);
        audioEncoder.Open(outFc.AudioCodec);
        outAudioStream.Codecpar!.CopyFrom(audioEncoder);

        // begin write
        using var io = IOContext.WriteStream(outputStream);
        outFc.Pb = io;
        outFc.WriteHeader();

        var decodingQueue = inFc
            .ReadPackets(inAudioStream.Index)
            .DecodeAllPackets(inFc, audioDecoder);

        var encodingQueue = decodingQueue
            .AudioFifo(audioEncoder)
            .EncodeAllFrames(outFc, audioEncoder);

        Dictionary<int, PtsDts> ptsDts = new();
        encodingQueue
            .RecordPtsDts(ptsDts)
            .WriteAll(outFc);
        outFc.WriteTrailer();

        return true;
    }
}
