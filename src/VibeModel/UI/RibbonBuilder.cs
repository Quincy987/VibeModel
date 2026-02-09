using System;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;
using VibeModel.Infrastructure;

namespace VibeModel.UI
{
    public static class RibbonBuilder
    {
        public static void CreateRibbon(UIControlledApplication application)
        {
            try
            {
                var tabName = "VibeModel";

                // Create tab (may already exist)
                try
                {
                    application.CreateRibbonTab(tabName);
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException)
                {
                    // Tab already exists — that's fine
                }

                var panel = application.CreateRibbonPanel(tabName, "AI Chat");

                var assemblyPath = Assembly.GetExecutingAssembly().Location;

                var buttonData = new PushButtonData(
                    "ShowVibeModelChat",
                    "Chat",
                    assemblyPath,
                    typeof(ShowChatCommand).FullName)
                {
                    ToolTip = "Open the VibeModel AI Chat panel to talk to Claude inside Revit",
                    LongDescription = "Opens a dockable chat panel that connects to Claude Code. " +
                                      "You can ask Claude to inspect and modify your Revit model using natural language."
                };

                // Try to load embedded icons
                var largeIcon = LoadEmbeddedIcon("chat-32.png");
                var smallIcon = LoadEmbeddedIcon("chat-16.png");

                if (largeIcon != null)
                    buttonData.LargeImage = largeIcon;
                if (smallIcon != null)
                    buttonData.Image = smallIcon;

                panel.AddItem(buttonData);

                Logger.Info("Ribbon created: VibeModel tab + Chat button");
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to create ribbon", ex);
            }
        }

        private static BitmapImage LoadEmbeddedIcon(string resourceName)
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var fullName = "VibeModel.UI.Resources." + resourceName;

                using (var stream = assembly.GetManifestResourceStream(fullName))
                {
                    if (stream == null)
                    {
                        Logger.Warn("Embedded resource not found: " + fullName);
                        return null;
                    }

                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.StreamSource = stream;
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to load icon: " + resourceName, ex);
                return null;
            }
        }
    }
}
