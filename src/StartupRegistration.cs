using System;
using System.IO;
using System.Reflection;
using System.Security;
using Microsoft.Win32;

namespace OrbitEvolved
{
    internal sealed class StartupRegistration
    {
        private readonly string keyPath;

        public StartupRegistration(string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run")
        {
            this.keyPath = keyPath;
        }

        public string Command
        {
            get { return "\"" + Assembly.GetEntryAssembly().Location + "\""; }
        }

        public bool TryRead(out string command)
        {
            command = null;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
                {
                    if (key != null)
                        command = key.GetValue(Product.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                }
                return true;
            }
            catch (IOException) { return false; }
            catch (SecurityException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        public string SetEnabled(bool enabled)
        {
            if (enabled && Command.Length > 260)
                return "Move the app to a shorter path before enabling startup.";

            const string error = "Windows could not change this startup setting.";
            try
            {
                if (enabled)
                {
                    using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
                    {
                        if (key == null) return error;
                        key.SetValue(Product.Name, Command, RegistryValueKind.String);
                    }
                }
                else
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(keyPath, true))
                    {
                        if (key != null) key.DeleteValue(Product.Name, false);
                    }
                }
                return null;
            }
            catch (IOException) { return error; }
            catch (SecurityException) { return error; }
            catch (UnauthorizedAccessException) { return error; }
        }
    }
}
