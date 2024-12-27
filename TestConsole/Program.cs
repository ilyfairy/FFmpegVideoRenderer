#pragma warning disable IDE0051 // Remove unused private members
using System.Diagnostics;
using System.Runtime.InteropServices;
using FFmpegVideoRenderer;
using Sdcb.FFmpeg.Codecs;
using Sdcb.FFmpeg.Formats;
using Sdcb.FFmpeg.Raw;
using Sdcb.FFmpeg.Toolboxs.Extensions;
using SkiaSharp;
using Spectre.Console;


internal class Program
{
    private static async Task Main(string[] args)
    {
        await WatchFFmpegDecode();
        Console.WriteLine("OK");
    }

    public static async Task WatchFFmpegDecode()
    {
        var input = "/mnt/z/6ef0d91afb13f477cbae9e0822363a99.mp4";
        var outputDir = @"/mnt/z/6ef0d91afb13f477cbae9e0822363a99/frames";
        Directory.CreateDirectory(outputDir);
        using FileSystemWatcher watcher = new(outputDir);
        int outputCount = 0;
        watcher.Created += (sender, e) =>
        {
            outputCount++;
            if (outputCount % 100 == 0)
            {
                Console.WriteLine($"ffmpeg已解码{outputCount}帧");
            }
        };
        watcher.IncludeSubdirectories = true;
        watcher.EnableRaisingEvents = true;

        var proc = Process.Start(new ProcessStartInfo()
        {
            FileName = "ffmpeg",
            Arguments = $"""
            -i "{input}" "{outputDir}/frame%d.jpg"
            """,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        _ = Task.Run(() =>
        {
            while (!proc.StandardOutput.EndOfStream)
            {
                var line = proc.StandardOutput.ReadLine();
                Console.WriteLine($"[ffmpeg] {line}");
            }
        });
        _ = Task.Run(() =>
        {
            while (!proc.StandardError.EndOfStream)
            {
                var line = proc.StandardError.ReadLine();
                Console.WriteLine($"[ffmpeg] {line}");
            }
        });
        await proc.WaitForExitAsync();
    }

    public static async Task Test1()
    {
        using var audioToDecode =
            File.OpenRead(@"E:\CloudMusic\MV\Erdenebat Baatar,Lkhamragchaa Lkhagvasuren,Altanjargal - Goyo (feat. Lkhamragchaa Lkhagvasuren, Altanjargal, Erdenechimeg G, Narandulam, Dashnyam & Uul Us).mp4");
        using var audioSourceToDecode = MediaSource.Create(audioToDecode);
        var pcm = new List<float>();

        for (int i = 0; i < 60; i++)
        {
            for (int j = 0; j < 22050; j++)
            {
                var time = TimeSpan.FromSeconds(i + j / (22050.0));
                if (audioSourceToDecode.GetAudioSample(time) is AudioSample sample)
                {
                    pcm.Add(sample.LeftValue);
                    //pcm.Add(sample.RightValue);
                }
                else
                {
                    pcm.Add(0);
                    //pcm.Add(0);
                }
            }
        }

        //pcm = MediaSource.s_pcmLeftSamples;

        var pcmArray = pcm.ToArray();
        var pcmSpan = pcmArray.AsSpan();
        var pcmByteSpan = MemoryMarshal.AsBytes(pcmSpan);

        File.WriteAllBytes("output.pcm", pcmByteSpan.ToArray());
    }


    static async Task VideoToAudio()
    {
        MemoryStream audioOutput = new();
        var input = File.OpenRead(@"D:\Downloads\Edge\S00125_CCM_13892271_2024-12-09_09-40-08.mp4");
        await ToAudioStream(input, audioOutput);
        Console.WriteLine("完成");
        Console.ReadLine();

        return;
        static async Task<bool> ToAudioStream(Stream videoStream, Stream outputStream)
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
                .DecodeAllPackets(inFc, audioDecoder)
                .ToThreadQueue(boundedCapacity: 64);

            var encodingQueue = decodingQueue.GetConsumingEnumerable()
                .AudioFifo(audioEncoder)
                .EncodeAllFrames(outFc, audioEncoder)
                .ToThreadQueue();

            CancellationTokenSource end = new();
            Dictionary<int, PtsDts> ptsDts = new();
            encodingQueue.GetConsumingEnumerable()
                .RecordPtsDts(ptsDts)
                .WriteAll(outFc);
            await end.CancelAsync();
            outFc.WriteTrailer();

            return true;
        }
    }

    static async Task TestMediaSource()
    {
        var mediaPath = @"E:\CloudMusic\MV\ナナツカゼ,PIKASONIC,なこたんまる - 春めく.mp4";
        var mediaStream = File.OpenRead(mediaPath);
        MediaSource mediaSource = MediaSource.Create(mediaStream);

        using var bitmap = new SKBitmap(mediaSource.VideoFrameWidth, mediaSource.VideoFrameHeight, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var stopwatch = Stopwatch.StartNew();

        int i = 0;
        while (true)
        {
            //var ms = stopwatch.ElapsedMilliseconds;
            i += 30;
            if (i < 0)
                i = 0;

            if (mediaSource.GetVideoFrame(stopwatch.Elapsed) is VideoFrame frame)
            {
                frame.FillBitmap(bitmap);


                var canvasImage = new CanvasImage(bitmap.Encode(SKEncodedImageFormat.Jpeg, 100).AsSpan());
                Console.SetCursorPosition(0, 0);
                AnsiConsole.Write(canvasImage);
            }
        }


        Console.WriteLine("Done.");
        Console.ReadKey(true);

    }

    static async Task TestRendering()
    {
        var video1 = @"E:\CloudMusic\MV\Erdenebat Baatar,Lkhamragchaa Lkhagvasuren,Altanjargal - Goyo (feat. Lkhamragchaa Lkhagvasuren, Altanjargal, Erdenechimeg G, Narandulam, Dashnyam & Uul Us).mp4";
        var video2 = @"E:\CloudMusic\MV\ナナツカゼ,PIKASONIC,なこたんまる - 春めく.mp4";
        var audio1 = @"E:\CloudMusic\KSHMR,Mark Sixma - Gladiator (Remix).mp3";
        var image = @"C:\Users\SlimeNull\OneDrive\Pictures\Desktop\452ed6a06123465397510ef74da830e1.jpg";
        using var output = new FileStream("output.mp4", FileMode.Create, FileAccess.ReadWrite, FileShare.Read);

        var project = new Project()
        {
            OutputWidth = 800,
            OutputHeight = 600,
            Resources =
            {
                new ProjectResource("1", () => File.OpenRead(video1)),
                new ProjectResource("2", () => File.OpenRead(video2)),
                new ProjectResource("bgm", () => File.OpenRead(audio1)),
                new ProjectResource("image", () => File.OpenRead(image)),
            },
            VideoTracks =
            {
                new VideoTrack()
                {
                    Children =
                    {
                        new VideoTrackItem()
                        {
                            ResourceId = "1",
                            Offset = TimeSpan.FromSeconds(0),
                            StartTime = TimeSpan.FromSeconds(0),
                            EndTime = TimeSpan.FromSeconds(6),
                            Volume = 0
                        },
                        new VideoTrackItem()
                        {
                            ResourceId = "2",
                            Offset = TimeSpan.FromSeconds(4),
                            StartTime = TimeSpan.FromSeconds(0),
                            EndTime = TimeSpan.FromSeconds(30),
                            Volume = 0
                        }
                    }
                },
                new VideoTrack()
                {
                    Children =
                    {
                        new VideoTrackItem()
                        {
                            ResourceId = "image",
                            StartTime = TimeSpan.FromSeconds(0),
                            EndTime = TimeSpan.FromSeconds(90)
                        }
                    }
                }
            },
            AudioTracks =
            {
                new AudioTrack()
                {
                    Children =
                    {
                        new AudioTrackItem()
                        {
                            ResourceId = "bgm",
                            StartTime = TimeSpan.FromSeconds(70),
                            EndTime = TimeSpan.FromSeconds(264)
                        }
                    }
                }
            }
        };

        await VideoRenderer.Render(project, output, null);
    }
}