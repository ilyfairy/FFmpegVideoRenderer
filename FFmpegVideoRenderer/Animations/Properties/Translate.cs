using FFmpegVideoRenderer.Abstraction;

namespace FFmpegVideoRenderer.Animations.Properties
{
    public record struct Translate : ILerpable<Translate>
    {
        public double OffsetX { get; set; }
        public double OffsetY { get; set; }

        public Translate(double offsetX, double offsetY)
        {
            OffsetX = offsetX;
            OffsetY = offsetY;
        }

        public static Translate Lerp(Translate first, Translate second, double t)
        {
            return new Translate(
                double.Lerp(first.OffsetX, second.OffsetX, t),
                double.Lerp(first.OffsetY, second.OffsetY, t));
        }
    }
}
