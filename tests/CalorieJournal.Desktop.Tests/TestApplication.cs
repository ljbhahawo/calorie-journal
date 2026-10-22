using System.Windows;

namespace CalorieJournal.Desktop.Tests;

internal sealed class TestApplication : Application
{
    public TestApplication()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/CalorieJournal;component/SharedStyles.xaml", UriKind.Relative)
        });
    }
}
