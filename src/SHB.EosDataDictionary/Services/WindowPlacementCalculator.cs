using System.Windows;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260903 统一计算窗口最大化和拖动恢复位置，确保窗口始终处于当前显示器工作区。</summary>
    internal static class WindowPlacementCalculator
    {
        /// <summary>XMZADD 20260903 将屏幕工作区转换为显示器相对坐标，供无边框窗口避开任务栏最大化。</summary>
        internal static Rect CalculateMaximizedBounds(Rect monitorBounds, Rect workingArea)
        {
            return new Rect(
                workingArea.Left - monitorBounds.Left,
                workingArea.Top - monitorBounds.Top,
                workingArea.Width,
                workingArea.Height);
        }

        /// <summary>XMZADD 20260903 按鼠标抓取位置恢复窗口，并限制窗口完整保留在当前显示器工作区。</summary>
        internal static Point CalculateRestoredLocation(
            Point cursorScreenPosition,
            Size restoredSize,
            double horizontalRatio,
            double titleBarOffset,
            Rect workingArea)
        {
            double clampedHorizontalRatio = Clamp(horizontalRatio, 0D, 1D);
            double clampedTitleBarOffset = Clamp(titleBarOffset, 0D, restoredSize.Height);
            double left = cursorScreenPosition.X - (restoredSize.Width * clampedHorizontalRatio);
            double top = cursorScreenPosition.Y - clampedTitleBarOffset;

            // 窗口大于工作区时固定到工作区起点，避免反向边界导致窗口定位到显示器外。
            if (restoredSize.Width > workingArea.Width)
            {
                left = workingArea.Left;
            }
            else
            {
                left = Clamp(left, workingArea.Left, workingArea.Right - restoredSize.Width);
            }

            if (restoredSize.Height > workingArea.Height)
            {
                top = workingArea.Top;
            }
            else
            {
                top = Clamp(top, workingArea.Top, workingArea.Bottom - restoredSize.Height);
            }

            return new Point(left, top);
        }

        /// <summary>XMZADD 20260903 将窗口定位参数限制在有效范围，避免无效抓取比例或偏移破坏工作区约束。</summary>
        private static double Clamp(double value, double min, double max)
        {
            if (value < min)
            {
                return min;
            }

            if (value > max)
            {
                return max;
            }

            return value;
        }
    }
}
