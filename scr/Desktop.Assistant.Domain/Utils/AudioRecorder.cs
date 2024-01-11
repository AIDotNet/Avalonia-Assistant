using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NAudio.Wave;
using System;

namespace Desktop.Assistant.Domain.Utils
{
    public class AudioRecorder
    {
        private WaveInEvent waveSource = null;
        private WaveFileWriter waveFile = null;

        public void StartRecording(string outputFilePath)
        {
            waveSource = new WaveInEvent();
            waveSource.WaveFormat = new WaveFormat(16000, 1); // 设置录音的格式，这里为 CD 质量

            waveSource.DataAvailable += new EventHandler<WaveInEventArgs>(WaveSource_DataAvailable);
            waveSource.RecordingStopped += new EventHandler<StoppedEventArgs>(WaveSource_RecordingStopped);

            waveFile = new WaveFileWriter(outputFilePath, waveSource.WaveFormat);

            waveSource.StartRecording();
        }

        private void WaveSource_DataAvailable(object sender, WaveInEventArgs e)
        {
            if (waveFile != null)
            {
                waveFile.Write(e.Buffer, 0, e.BytesRecorded);
                waveFile.Flush();
            }
        }

        private void WaveSource_RecordingStopped(object sender, StoppedEventArgs e)
        {
            if (waveSource != null)
            {
                waveSource.Dispose();
                waveSource = null;
            }

            if (waveFile != null)
            {
                waveFile.Dispose();
                waveFile = null;
            }
        }

        public void StopRecording()
        {
            waveSource.StopRecording();
        }
    }
}
