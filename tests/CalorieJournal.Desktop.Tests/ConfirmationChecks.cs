using System.Runtime.InteropServices;
using System.Text;

namespace CalorieJournal.Desktop.Tests;

internal static class ConfirmationChecks
{
    // 只检查当前测试线程创建的真实原生确认框，自动选择“是/否”；不替换确认逻辑或访问用户窗口。
    public static int Respond(Action action, bool accept) => RespondChoice(action, accept ? 6 : 7, "请确认");
    public static int RespondChoice(Action action, int choice, string expectedTitle = "新食物资料")
    {
        var thread = GetCurrentThreadId();
        using var stop = new CancellationTokenSource();
        var windows = new HashSet<nint>();
        var responder = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                EnumThreadWindows(thread, (window, _) =>
                {
                    var title = new StringBuilder(128); GetWindowText(window, title, title.Capacity);
                    var className = new StringBuilder(128); GetClassName(window, className, className.Capacity);
                    if (title.ToString() == expectedTitle && className.ToString() == "#32770")
                    {
                        windows.Add(window);
                        PostMessage(window, 0x0111, choice, 0);
                    }
                    return true;
                }, 0);
                Thread.Sleep(10);
            }
        });
        try { action(); }
        finally { stop.Cancel(); responder.GetAwaiter().GetResult(); }
        return windows.Count;
    }

    private delegate bool EnumWindow(nint window, nint parameter);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumThreadWindows(uint thread, EnumWindow callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder text, int count);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
