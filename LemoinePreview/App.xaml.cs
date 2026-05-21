using System.Windows;

namespace LemoineTools.Preview
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            new PreviewMainWindow().Show();
        }
    }
}
