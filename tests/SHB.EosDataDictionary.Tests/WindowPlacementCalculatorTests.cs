using System.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    /// <summary>XMZADD 20260903 验证窗口定位几何计算在多显示器及拖动恢复场景下遵守工作区边界。</summary>
    [TestClass]
    public sealed class WindowPlacementCalculatorTests
    {
        /// <summary>XMZADD 20260903 验证底部任务栏占用空间时最大化窗口仅覆盖显示器工作区。</summary>
        [TestMethod]
        public void CalculateMaximizedBounds_BottomTaskbar_UsesWorkingArea()
        {
            var monitorBounds = new Rect(0D, 0D, 2560D, 1440D);
            var workingArea = new Rect(0D, 0D, 2560D, 1400D);

            Rect actual = WindowPlacementCalculator.CalculateMaximizedBounds(monitorBounds, workingArea);

            Assert.AreEqual(0D, actual.X, 0.01D);
            Assert.AreEqual(0D, actual.Y, 0.01D);
            Assert.AreEqual(2560D, actual.Width, 0.01D);
            Assert.AreEqual(1400D, actual.Height, 0.01D);
        }

        /// <summary>XMZADD 20260903 验证左侧任务栏位于副显示器时最大化边界转换为相对显示器坐标。</summary>
        [TestMethod]
        public void CalculateMaximizedBounds_SecondaryMonitorWithLeftTaskbar_UsesRelativeCoordinates()
        {
            var monitorBounds = new Rect(-1920D, 0D, 1920D, 1080D);
            var workingArea = new Rect(-1880D, 0D, 1880D, 1040D);

            Rect actual = WindowPlacementCalculator.CalculateMaximizedBounds(monitorBounds, workingArea);

            Assert.AreEqual(40D, actual.X, 0.01D);
            Assert.AreEqual(0D, actual.Y, 0.01D);
            Assert.AreEqual(1880D, actual.Width, 0.01D);
            Assert.AreEqual(1040D, actual.Height, 0.01D);
        }

        /// <summary>XMZADD 20260903 验证鼠标靠近工作区边缘恢复窗口时仍将整个窗口约束在可见范围内。</summary>
        [TestMethod]
        public void CalculateRestoredLocation_EdgePointer_ClampsWindowInsideWorkingArea()
        {
            var pointer = new Point(2500D, 10D);
            var restoredSize = new Size(1600D, 980D);
            var workingArea = new Rect(0D, 0D, 2560D, 1400D);

            Point actual = WindowPlacementCalculator.CalculateRestoredLocation(
                pointer,
                restoredSize,
                0.95D,
                20D,
                workingArea);

            Assert.AreEqual(960D, actual.X, 0.01D);
            Assert.AreEqual(0D, actual.Y, 0.01D);
        }

        /// <summary>XMZADD 20260903 验证负坐标副屏恢复窗口时使用该副屏工作区边界，而非错误回到主屏。</summary>
        [TestMethod]
        public void CalculateRestoredLocation_NegativeWorkingArea_StaysOnSecondaryMonitor()
        {
            var pointer = new Point(-100D, 80D);
            var restoredSize = new Size(1200D, 800D);
            var workingArea = new Rect(-1920D, 0D, 1920D, 1040D);

            Point actual = WindowPlacementCalculator.CalculateRestoredLocation(
                pointer,
                restoredSize,
                0.5D,
                20D,
                workingArea);

            Assert.AreEqual(-1200D, actual.X, 0.01D);
            Assert.AreEqual(60D, actual.Y, 0.01D);
        }

        /// <summary>XMZADD 20260903 验证恢复窗口大于工作区时固定到工作区起点，避免产生反向边界坐标。</summary>
        [TestMethod]
        public void CalculateRestoredLocation_WindowLargerThanWorkingArea_UsesWorkingAreaOrigin()
        {
            var pointer = new Point(900D, 500D);
            var restoredSize = new Size(1800D, 1100D);
            var workingArea = new Rect(100D, 40D, 1600D, 900D);

            Point actual = WindowPlacementCalculator.CalculateRestoredLocation(
                pointer,
                restoredSize,
                0.5D,
                20D,
                workingArea);

            Assert.AreEqual(100D, actual.X, 0.01D);
            Assert.AreEqual(40D, actual.Y, 0.01D);
        }
    }
}
