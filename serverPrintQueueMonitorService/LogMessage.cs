using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace serverPrintQueueMonitorService
{
    public class LogMessage
    {
        private TextBox logTextBox;
        public LogMessage(TextBox tb) 
        {
            logTextBox = tb;
        }

        public void Print(string message)
        {
            message = $"[{DateTime.Now.ToString("HH:mm:ss")}] {message}";
            if (logTextBox.InvokeRequired)
            {
                logTextBox.Invoke(new Action<string>(Print), message);
            }
            else
            {
                logTextBox.AppendText(message + Environment.NewLine);
            }
        }
    }
}
