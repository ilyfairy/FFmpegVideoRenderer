using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using FFmpegVideoRenderer.Abstraction;

namespace FFmpegVideoRenderer.Animations
{
    public struct KeyFrame<T>
        where T : ILerpable<T>
    {
        public TimeSpan Offset { get; set; }
        public IEasingFunction? EasingFunction { get; set; }
        public T Value { get; set; }
    }
}
