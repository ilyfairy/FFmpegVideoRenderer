using FFmpegVideoRenderer.Abstraction;

namespace FFmpegVideoRenderer.Animations.Properties
{
    public record struct Scale : ILerpable<Scale>
    {
        public double ScaleX { get; set; }
        public double ScaleY { get; set; }

        public Scale(double scaleX, double scaleY)
        {
            ScaleX = scaleX;
            ScaleY = scaleY;
        }

        public static Scale Lerp(Scale first, Scale second, double t)
        {
            return new Scale(
                double.Lerp(first.ScaleX, second.ScaleX, t),
                double.Lerp(first.ScaleY, second.ScaleY, t));
        }
    }
}
