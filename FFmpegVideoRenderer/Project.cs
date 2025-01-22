namespace FFmpegVideoRenderer
{
    public class Project
    {
        public string? Name { get; set; }

        public List<ProjectResource> Resources { get; } = new();

        public List<AudioTrackLine> AudioTracks { get; } = new();
        public List<VideoTrackLine> VideoTracks { get; } = new();

        public int OutputWidth { get; set; }
        public int OutputHeight { get; set; }
    }
}
