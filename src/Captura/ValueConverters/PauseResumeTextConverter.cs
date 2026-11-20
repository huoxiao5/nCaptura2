using System;
using System.Windows.Data;
using System.Globalization;
using Captura.Models;

namespace Captura
{
    public class PauseResumeTextConverter : IValueConverter
    {
        public object Convert(object Value, Type TargetType, object Parameter, CultureInfo Culture)
        {
            if (Value is RecorderState state)
            {
                return state == RecorderState.Paused ? "继续" : "暂停";
            }
            return "暂停";
        }

        public object ConvertBack(object Value, Type TargetType, object Parameter, CultureInfo Culture)
        {
            throw new NotImplementedException();
        }
    }
}
