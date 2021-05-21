using System;

namespace ScreenCapture.Events
{
    public class ScreenCaptureUpdatedEventArgs : EventArgs
    {
        #region Properties & Fields

        public bool IsSuccessful { get; set; }

        #endregion

        #region Constructors

        public ScreenCaptureUpdatedEventArgs(bool isSuccessful)
        {
            this.IsSuccessful = isSuccessful;
        }

        #endregion
    }
}
