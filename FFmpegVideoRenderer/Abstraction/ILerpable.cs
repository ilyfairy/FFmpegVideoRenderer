using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FFmpegVideoRenderer.Abstraction
{
    public interface ILerpable<T>
    {
        public static abstract T Lerp(T first, T second, double t);
    }
}
