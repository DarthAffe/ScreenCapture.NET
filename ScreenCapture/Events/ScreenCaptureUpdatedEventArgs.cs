// ReSharper disable MemberCanBePrivate.Global
using System;

namespace ScreenCapture
{
    /// <inheritdoc />
    /// <summary>
    /// Represents the information supplied with an <see cref="E:ScreenCapture.IDX11ScreenCapture.Updated" />-event.
    /// </summary>
    public class ScreenCaptureUpdatedEventArgs : EventArgs
    {
        #region Properties & Fields

        /// <summary>
        /// <c>true</c> if the update was successful; otherwise, <c>false</c>.
        /// </summary>
        public bool IsSuccessful { get; set; }

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="ScreenCaptureUpdatedEventArgs"/> class.
        /// </summary>
        /// <param name="isSuccessful">Indicates if the last update was successful.</param>
        public ScreenCaptureUpdatedEventArgs(bool isSuccessful)
        {
            this.IsSuccessful = isSuccessful;
        }

        #endregion
    }
}
