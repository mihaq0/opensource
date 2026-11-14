using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NightRun;

sealed class SettingsStandalone : Form
{
    readonly NumericUpDown nFov;
    public SettingsStandalone()
    {
        Text = "NightRun settings";
        Width = 320; Height = 120;
        nFov = new NumericUpDown { Minimum = 20, Maximum = 900, Value = Cfg.S.AimFov, Left = 10, Top = 10, Width = 80 };
        var b = new Button { Text = "Save", Left = 10, Top = 40, Width = 80 };
        b.Click += (s, e) => { Cfg.S.AimFov = (int)nFov.Value; Cfg.Touch(); Cfg.Save(); Close(); };
        Controls.Add(nFov); Controls.Add(b);
    }
}
