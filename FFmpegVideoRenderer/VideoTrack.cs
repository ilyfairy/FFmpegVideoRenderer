namespace FFmpegVideoRenderer
{
    public class VideoTrackItem : TrackItem
    {
        public int PositionX { get; set; }
        public int PositionY { get; set; }
        public int SizeWidth { get; set; }
        public int SizeHeight { get; set; }

        public AudioTrackItem ToAudioTrackItem()
        {
            return new AudioTrackItem()
            {
                ResourceId = ResourceId,
                Offset = Offset,
                StartTime = StartTime,
                EndTime = EndTime,
                Volume = Volume,
            };
        }
    }

    public class VideoTrackLine : TrackLine<VideoTrackItem>
    {
        public bool MuteAudio { get; set; }
    }
}
