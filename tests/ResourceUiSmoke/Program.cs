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
            foreach (var fixture in new[] { "Resources.xaml", "Datapacks.xaml" })
                _VerifySelectionBar(fixture);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void _VerifySelectionBar(string fixture)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var source = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));
        var cardXml = new XElement(source.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "CardSelect"));
        cardXml.Attribute("SizeChanged")!.Remove();
        cardXml.SetAttributeValue(XNamespace.Xmlns + "x", x.NamespaceName);
        var local = XNamespace.Get("clr-namespace:PCL;assembly=" + typeof(MyCard).Assembly.GetName().Name);
        // 从实际页面提取卡片，加载实际 WPF 控件，避免测试与界面布局脱节。
        foreach (var element in cardXml.DescendantsAndSelf())
            if (element.Name.NamespaceName == "clr-namespace:PCL") element.Name = local + element.Name.LocalName;
        cardXml.SetAttributeValue(XNamespace.Xmlns + "local", local.NamespaceName);
        var card = (MyCard)XamlReader.Parse(cardXml.ToString());
        card.Visibility = Visibility.Visible;
        card.Opacity = 1;
        var undo = (MyIconTextButton)card.FindName("BtnSelectUndo");
        var actions = (WrapPanel)card.FindName("PanSelectActions");
        var refresh = typeof(MyCard).Assembly.GetType("PCL.ResourceUpdateUndo")!
            .GetMethod("RefreshSelectionButton", BindingFlags.Public | BindingFlags.Static)!;
        refresh.Invoke(null, new object[] { undo, 0, false });
        _Assert(undo.Visibility == Visibility.Collapsed, "No eligible selection must hide undo");
        refresh.Invoke(null, new object[] { undo, 1, false });
        _Assert(undo.Visibility == Visibility.Visible && undo.Text == "撤回 (1)", "Mixed selection must show eligible count");
        refresh.Invoke(null, new object[] { undo, 2, true });
        _Assert(!undo.IsEnabled, "In-progress undo must reject repeated clicks");
        refresh.Invoke(null, new object[] { undo, 2, false });

        var host = new Grid();
        host.Children.Add(card);
        double wideHeight = 0;
        foreach (var width in new[] { 900d, 600d, 360d })
        {
            host.Measure(new Size(width, 600));
            host.Arrange(new Rect(0, 0, width, 600));
            host.UpdateLayout();
            if (width == 900) wideHeight = card.ActualHeight;
            _Assert(card.ActualWidth <= width - 50 + 0.1, "Card must stay within page margins");
            foreach (FrameworkElement button in actions.Children)
            {
                var point = button.TranslatePoint(new Point(), actions);
                _Assert(point.X >= -0.1 && point.X + button.ActualWidth <= actions.ActualWidth + 0.1,
                    "All actions must fit horizontally, including cancel");
                _Assert(point.Y + button.ActualHeight <= actions.ActualHeight + 0.1, "All wrapped actions must fit vertically");
            }
            if (width == 360) _Assert(card.ActualHeight > wideHeight, "Narrow windows must wrap actions");
        }
        refresh.Invoke(null, new object[] { undo, 0, false });
        _Assert(undo.Visibility == Visibility.Collapsed, "Undo must disappear after all selected updates are restored");
        Console.WriteLine("PASS: " + fixture + " selection visibility, eligible count, busy state and 900/600/360px layout");
    }

    private static void _Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
