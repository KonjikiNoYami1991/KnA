using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Threading;

namespace TnA___Tanoshimi_no_Autohardsubber
{
    static class Program
    {
        /// <summary>
        /// Punto di ingresso principale dell'applicazione.
        /// </summary>
        /// 

        private static string appGuid = "09de345e-3bd7-41da-a159-c5974c9a5591";

        [STAThread]
        static void Main()
        {
            using (Mutex mutex = new Mutex(false, "Global\\" + appGuid))
            {
                if (!mutex.WaitOne(0, false))
                {
                    MessageBox.Show("Un'istanza dell'applicazione è già presente. Non è possibile avviarne un'altra.", "Tanoshimi no Autohardsubber", MessageBoxButtons.OK, MessageBoxIcon.Stop);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TnA());
            }
        }
    }
}
