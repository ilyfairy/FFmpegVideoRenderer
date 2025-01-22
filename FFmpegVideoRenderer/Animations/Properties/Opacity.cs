using FFmpegVideoRenderer.Abstraction;

namespace FFmpegVideoRenderer.Animations.Properties
{
    public record struct Opacity : ILerpable<Opacity>
    {
        public double Value { get; set; }

        public Opacity(double value)
        {
            Value = value;
        }

        public static Opacity Lerp(Opacity first, Opacity second, double t)
        {
            return new Opacity(double.Lerp(first.Value, second.Value, t));
        }

        public static implicit operator Opacity(double value)
        {
            return new Opacity(value);
        }

        public static implicit operator double(Opacity opacity)
        {
            return opacity.Value;
        }
    }
}
