namespace FFmpegVideoRenderer
{
    public class TrackLine<TTrackItem> where TTrackItem : TrackItem
    {
        public List<TTrackItem> Children { get; } = new();
    }
}
