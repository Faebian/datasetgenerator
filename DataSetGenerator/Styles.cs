using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DataSetGenerator
{
    public static class Styles
    {

        public static bool isDarkMode = true;
        public static bool isGreenMode = true;

        // Dark theme
        public static Color BackColorDark = Color.FromArgb(50, 50, 50);
        public static Color SelectedBackColorDark = Color.FromArgb(90, 90, 90);
        public static Color ForeColorDark = Color.White;
        public static Color MessageSuccessColorDark = Color.LimeGreen;
        public static Color MessageWarningColorDark = Color.Orange;
        public static Color MessageErrorColorDark = Color.PaleVioletRed;
        public static Color MessageMutedColorDark = Color.LightGray;
        public static Color MessageDefaultColorDark = Color.White;
        public static Color MenuColorDark = Color.FromArgb(23, 21, 18);
        public static Color ImageColorDark = Color.FromArgb(50, 50, 50);
        public static Color ScriptBackColorDark = Color.FromArgb(30, 30, 30);
        public static Color ScriptForeColorDark = Color.White;
        public static Color DashboardTileDark = Color.FromArgb(36, 36, 42);
        public static Color FormTileLetterBoxDark = Color.FromArgb(45, 45, 52);

        // Light theme
        public static Color BackColorLight = Color.FromArgb(220, 220, 220);
        public static Color SelectedBackColorLight = Color.FromArgb(197, 197, 197);
        public static Color ForeColorLight = Color.Black;
        public static Color MessageSuccessColorLight = Color.DarkGreen;
        public static Color MessageWarningColorLight = Color.DarkOrange;
        public static Color MessageErrorColorLight = Color.DarkRed;
        public static Color MessageMutedColorLight = Color.FromArgb(30, 30, 30);
        public static Color MessageDefaultColorLight = Color.Black;
        public static Color MenuColorLight = Color.White;
        public static Color ImageColorLight = Color.FromArgb(220, 220, 220);
        public static Color ScriptBackColorLight = Color.White;
        public static Color ScriptForeColorLight = Color.Black;
        public static Color DashboardTileLight = Color.White;
        public static Color FormTileLetterBoxLight = Color.FromArgb(45, 45, 52);

        // Shiny button themes
        public static Color GradientStartGreen = Color.LimeGreen;
        public static Color GradientEndGreen = Color.DarkGreen;
        public static Color GradientStartBlue = Color.CornflowerBlue;
        public static Color GradientEndBlue = Color.DarkBlue;
        public static Color GradientStartDark = Color.FromArgb(50, 50, 50);
        public static Color GradientEndDark = Color.FromArgb(40, 40, 40);
        public static Color BorderDark = Color.FromArgb(60, 60, 60);
        public static Color GradientStartDisabled = Color.FromArgb(200, 200, 200);
        public static Color GradientEndDisabled = Color.FromArgb(180, 180, 180);
        public static Color GradientStartLight = Color.FromArgb(220, 220, 220);
        public static Color GradientEndLight = Color.FromArgb(200, 200, 200);
        public static Color BorderLight = Color.FromArgb(240, 240, 240);

        // Tool styles
        public static Color RHEED_Intensity = Color.LimeGreen;
        public static Color BFM_BEP = Color.LimeGreen;
        public static Color BFM_FLUX = Color.CornflowerBlue;
        public static Color FLUX_FLUX = Color.LimeGreen;
        public static Color FLUX_SOURCE = Color.CornflowerBlue;
        public static Color FLUX_SUB = Color.CornflowerBlue;

        // Image styles
        public static Color ImageGreen = Color.FromArgb(0, 176, 80);
        public static Color ImageBlue = Color.FromArgb(0, 112, 192);
        public static Color ImageDark = Color.FromArgb(50, 50, 50);
        public static Color ImageLight = Color.FromArgb(220, 220, 220);
    }
}
