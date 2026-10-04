using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Reflection;
using System.IO;
using System.Xml.Linq;
using System.Windows.Markup;
using PCL;
using PCL.Core.App.Configuration;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            // 只注册默认配置项，不启动服务或读取用户配置。
            var config = typeof(ConfigService);
            config.GetMethod("_InitializeConfigItems", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
            config.GetField("_isConfigItemsInitialized", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, true);
            var app = new System.Windows.Application();
            app.Resources["ColorBrushSemiTransparent"] = Brushes.Transparent;
            app.Resources["ColorBrush4"] = Brushes.Black;
            app.Resources["ColorBrush2"] = Brushes.Blue;
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/PCL.Core;component/App/Localization/Languages/zh-CN.xaml")
            });
            var item = new MyLocalCompItem();
            var delete = new MyIconButton();
            var disable = new MyIconButton();
            var undo = new MyIconButton();
            item.Buttons = new[] { disable, delete };
            var oldPanel = (StackPanel)item.buttonStack;

            // 更新后首次悬停：复用原有按钮并添加撤回按钮。
            item.Buttons = item.Buttons.Append(undo).ToArray();
            _Assert(oldPanel.Children.Count == 0, "Old panel must release its children");
            _Assert(((StackPanel)item.buttonStack).Children.Count == 3, "Undo must be added");
            _Assert(ReferenceEquals(delete.Parent, item.buttonStack), "Delete must belong to the new panel");
            Console.WriteLine("PASS: append undo to existing resource buttons");

            // 撤回后移除撤回按钮，其他按钮仍可复用。
            item.Buttons = item.Buttons.Where(button => button != undo);
            _Assert(undo.Parent is null, "Removed undo button must be detached");
            _Assert(((StackPanel)item.buttonStack).Children.Count == 2, "Only original buttons must remain");
            Console.WriteLine("PASS: remove undo after restoring resource");

            item.Buttons = item.Buttons;
            _Assert(((StackPanel)item.buttonStack).Children.Count == 2, "Reassigning the same buttons must work");
            item.Buttons = Array.Empty<MyIconButton>();
            _Assert(delete.Parent is null && disable.Parent is null, "Clearing must detach all buttons");
            _Assert(item.buttonStack is null, "Empty buttons must not retain a panel");
            Console.WriteLine("PASS: reassign and clear resource buttons");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void _Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
