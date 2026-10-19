using System;
using System.Windows.Forms;
using Discphew.UI;

[assembly: System.Reflection.AssemblyTitle("discphew")]
[assembly: System.Reflection.AssemblyProduct("discphew")]
[assembly: System.Reflection.AssemblyDescription("A lightweight, open-source utility for reading and writing CDs and DVDs.")]
[assembly: System.Reflection.AssemblyVersion("0.1.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("0.1.0.0")]

namespace Discphew
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Theme.Init();
            Application.ThreadException += (s, e) => Dialogs.Error(null, "Something went wrong", e.Exception.Message);
            Application.Run(new MainForm());
        }
    }
}
