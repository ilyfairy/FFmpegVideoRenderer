using System.Collections.ObjectModel;
using FFmpegVideoRenderer.Abstraction;

namespace FFmpegVideoRenderer.Animations
{
    public class KeyFrames<T> : Collection<KeyFrame<T>>
        where T : ILerpable<T>
    {
        public T DefaultValue { get; }

        public KeyFrames(T defaultValue)
        {
            DefaultValue = defaultValue;
        }

        private void GetPrevNextKeyFrame(TimeSpan offset, out KeyFrame<T>? prev, out KeyFrame<T>? next)
        {
            prev = null;
            next = null;

            foreach (var keyFrame in this)
            {
                if (keyFrame.Offset <= offset)
                {
                    prev = keyFrame;
                }
                else
                {
                    next = keyFrame;
                }

                if (next.HasValue)
                {
                    return;
                }
            }
        }

        public T Sample(TimeSpan offset)
        {
            GetPrevNextKeyFrame(offset, out var prev, out var next);

            if (!next.HasValue)
            {
                if (!prev.HasValue)
                {
                    return DefaultValue;
                }

                return prev.Value.Value;
            }
            else if (!prev.HasValue)
            {
                return DefaultValue;
            }

            var t = (offset - prev.Value.Offset) / (next.Value.Offset - prev.Value.Offset);
            t = next.Value.EasingFunction?.Ease(t) ?? t;

            return T.Lerp(prev.Value.Value, next.Value.Value, t);
        }
    }
}
