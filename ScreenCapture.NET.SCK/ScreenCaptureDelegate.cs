using System;
using System.Runtime.InteropServices;
using AudioToolbox;
using CoreMedia;
using CoreVideo;
using ObjCRuntime;
using ScreenCaptureKit;
using Serilog;

namespace ScreenCapture.NET.SCK;

public class ScreenCaptureDelegate : NSObject, ISCStreamOutput, INativeObject, IDisposable, ISCStreamDelegate
{
    public ScreenCaptureDelegate(byte[] buffer)
    {
        _buffer = buffer;
    }
    public event Action StreamStopped;
    public int BufferReceived { get; set; }
    private AudioBuffer[] _audioBuffer;
    private byte[] _buffer;
    [Export("init")]
    public ScreenCaptureDelegate()
    {

    }
    [Foundation.Export("stream:didOutputSampleBuffer:ofType:")]
    public unsafe void DidOutputSampleBuffer(SCStream stream, CMSampleBuffer sampleBuffer, SCStreamOutputType type)
    {

        try
        {
            if (type == SCStreamOutputType.Screen)
            {
                // Process video frame
                using (sampleBuffer)
                {
                    var imageBuffer = sampleBuffer.GetImageBuffer() as CVPixelBuffer;
                    if (imageBuffer != null)
                    {
                        using (imageBuffer)
                        {
                            imageBuffer.Lock(lockFlags: CVPixelBufferLock.ReadOnly);
                            IntPtr baseAddress = imageBuffer.BaseAddress;
                            int bytesPerRow = (int)imageBuffer.BytesPerRow;
                            int width = (int)imageBuffer.Width;
                            int height = (int)imageBuffer.Height;

                            Marshal.Copy(baseAddress, _buffer, 0, _buffer.Length);
                            imageBuffer.Unlock(CVPixelBufferLock.ReadOnly);
                        }
                    }
                }

            }
            else if (type == SCStreamOutputType.Audio)
            {
                // Process audio buffer
                var formatDescription = sampleBuffer.GetAudioFormatDescription();
                var numSamples = (int)sampleBuffer.NumSamples;
                _audioBuffer = new AudioBuffer[numSamples];
                AudioBuffers outputBuffer = new AudioBuffers(numSamples); // Replace '1' with the appropriate number of buffers required
                var error = sampleBuffer.CopyPCMDataIntoAudioBufferList(0, (int)sampleBuffer.NumSamples, outputBuffer);

                if (error != CMSampleBufferError.None)
                {
                    Log.Error(error.ToString());
                }
                else
                {
                    ProcessAudioBuffer(outputBuffer);
                    outputBuffer.Dispose();
                    sampleBuffer.Dispose();
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }

    }

    private unsafe void ProcessAudioBuffer(AudioBuffers audioBufferList)
    {
        for (int i = 0; i < audioBufferList.Count; i++)
        {

            var audioBuffer = audioBufferList[i];
            _audioBuffer[i] = audioBuffer;

            // IntPtr audioData = audioBufferList[i].Data;
            // int audioDataByteSize = audioBufferList[i].DataByteSize;

            // // Convert audio data to a float array for processing
            // float[] audioSamples = new float[audioDataByteSize / sizeof(float)];
            // Marshal.Copy(audioData, audioSamples, 0, audioSamples.Length);

            // // Perform further processing on the audioSamples array (e.g., FFT, visualization, etc.)
            // Console.WriteLine($"Processed {audioSamples.Length} audio samples.");
        }

    }
    [Export("stream:didStopWithError:")]
    void DidStop(SCStream stream, NSError error)
    {
        if (error != null)
        {
            Log.Error("Error while capturing screen: " + error.ToString());
            StreamStopped?.Invoke();
        }
    }
}
