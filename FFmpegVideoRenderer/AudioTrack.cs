using FFmpegVideoRenderer.Animations;
using FFmpegVideoRenderer.Animations.Properties;

namespace FFmpegVideoRenderer
{
    public class AudioTrackItem : TrackItem
    {
        public KeyFrames<Opacity> SoundKeyFrames { get; }

        public AudioTrackItem()
        {
            SoundKeyFrames = new KeyFrames<Opacity>(new Opacity(1));
        }
    }

    public class AudioTrackLine : TrackLine<AudioTrackItem>
    {

    }
}
