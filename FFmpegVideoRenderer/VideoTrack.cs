using FFmpegVideoRenderer.Animations;
using FFmpegVideoRenderer.Animations.Properties;

namespace FFmpegVideoRenderer
{
    public class VideoTrackItem : TrackItem
    {
        public int PositionX { get; set; }
        public int PositionY { get; set; }
        public int SizeWidth { get; set; }
        public int SizeHeight { get; set; }

        public KeyFrames<Opacity> SoundKeyFrames { get; }
        public KeyFrames<Opacity> OpacityKeyFrames { get; }
        public KeyFrames<Translate> TranslateKeyFrames { get; }
        public KeyFrames<Scale> ScaleKeyFrames { get; }

        public VideoTrackItem()
        {
            SoundKeyFrames = new KeyFrames<Opacity>(new Opacity(1));
            OpacityKeyFrames = new KeyFrames<Opacity>(new Opacity(1));
            TranslateKeyFrames = new KeyFrames<Translate>(default);
            ScaleKeyFrames = new KeyFrames<Scale>(new Scale(1, 1));
        }

        public AudioTrackItem ToAudioTrackItem()
        {
            var item = new AudioTrackItem()
            {
                ResourceId = ResourceId,
                Offset = Offset,
                StartTime = StartTime,
                EndTime = EndTime,
                Volume = Volume,
            };

            foreach (var soundKeyFrame in SoundKeyFrames)
            {
                item.SoundKeyFrames.Add(soundKeyFrame);
            }

            return item;
        }
    }

    public class VideoTrackLine : TrackLine<VideoTrackItem>
    {
        public bool MuteAudio { get; set; }
    }
}
