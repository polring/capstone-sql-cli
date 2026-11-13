using System;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace SqlApp.Converters
{
    public class BoolToVisibilityConverter : IValueConverter
    {
        public bool Invert { get; set; }

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            bool b = value is bool val && val;
            if (Invert) b = !b;
            return b ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    public class StringToFilenameConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is string path && !string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    string parent = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
                    string file = Path.GetFileName(path);
                    return string.IsNullOrEmpty(parent) ? file : $"{parent}/{file}";
                }
                catch
                {
                    return path;
                }
            }
            return "(선택 안 됨)";
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    public class SafeIntConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is int intVal)
            {
                return intVal.ToString();
            }
            return parameter?.ToString() ?? "0";
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            if (value is string str && int.TryParse(str.Trim(), out int result))
            {
                return result;
            }
            if (parameter is string paramStr && int.TryParse(paramStr, out int fallback))
            {
                return fallback;
            }
            return 0;
        }
    }
}

