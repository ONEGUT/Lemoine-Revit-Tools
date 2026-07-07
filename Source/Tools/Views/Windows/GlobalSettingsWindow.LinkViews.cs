using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Automation;
using System.Windows.Shapes;
using LemoineTools.Lemoine.Controls;
using LemoineTools.Tools.AutoFilters;
using LemoineTools.Tools.FiltersLegends.LegendCreator;
using LemoineTools.Tools.Ceilings;
using LemoineTools.Tools.LinkViews;

namespace LemoineTools.Lemoine
{
    // Link Views no longer has a global settings tab — its View Geometry settings
    // (XY buffer, cluster threshold, cut plane offset) are now per-run controls in
    // the tool's S1 "Source Documents" step. This partial is kept as a home for any
    // future Link Views-specific GlobalSettingsWindow methods.
    public partial class GlobalSettingsWindow
    {
    }
}
