﻿using SpeechLib;
using Module;
using System;
using System.Speech.Recognition;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Generic;
using NAudio;
using NAudio.Wave;
using Logging;
using System.IO;
using System.Speech.AudioFormat;
using NAudio.CoreAudioApi;

namespace Speech
{
    public class SpeechService : ServiceBase, IDisposable
    {
        public class RecognizeEvent
        {
            public string Text;

            public RecognizeEvent(string text)
            {
                Text = text;
            }
        }

        //public event EventHandler<RecognizeEvent> OnRecognizeEvent;

        private TimeSpan COMMAND_TIMEOUT = new TimeSpan(0, 0, 10);
        private DateTime mCommandTime = new DateTime();
        private Object mLock = new Object();
        private SpVoice mVoice;

        private SpeechRecognitionEngine mRecognizer;
        private Dictionary<string, DeviceBase.VoiceCommand> mVoiceCommands;
        private Queue<Grammar> mGrammarQueue = new Queue<Grammar>();

        private AudioInput mInput;

        public SpeechService(string name, ServiceCreationInfo info)
        : base("speech", info)
        {
            mVoice = new SpVoice();

            mVoiceCommands = new Dictionary<string, DeviceBase.VoiceCommand>();

            mInput = new AudioInput();

            mRecognizer = new SpeechRecognitionEngine();
            mRecognizer.SpeechRecognized += new EventHandler<SpeechRecognizedEventArgs>(OnSpeechRecognized);
            mRecognizer.RecognizerUpdateReached += new EventHandler<RecognizerUpdateReachedEventArgs>(OnUpdateRecognizer);
            mRecognizer.RecognizeCompleted += new EventHandler<RecognizeCompletedEventArgs>(OnRecognizeCompleted);

            var grammar = new Grammar(new GrammarBuilder(new Choices(new string[] { "computer" })));
            mRecognizer.LoadGrammar(grammar);

            var speechFormat = new SpeechAudioFormatInfo(44100, AudioBitsPerSample.Sixteen, AudioChannel.Mono);
            mRecognizer.SetInputToAudioStream(mInput.mStream, speechFormat);

            mRecognizer.RecognizeAsync(RecognizeMode.Multiple);
        }

        public void Dispose()
        {
            mRecognizer.Dispose();
            mInput.Dispose();            
        }

        public void LoadCommands(IList<DeviceBase.VoiceCommand> commands)
        {
            if (commands.Count == 0)
                return;

            lock (mLock)
            {
                var strings = new List<string>();
                foreach (var command in commands)
                {
                    mVoiceCommands.Add(command.Command, command);
                    strings.Add(command.Command);
                }

                Choices choices = new Choices();
                choices.Add(strings.ToArray());

                GrammarBuilder gb = new GrammarBuilder();
                gb.Append(choices);

                Grammar g = new Grammar(gb);
                mGrammarQueue.Enqueue(g);
            }

            mRecognizer.RequestRecognizerUpdate();
        }

        private void OnSpeechRecognized(object sender, SpeechRecognizedEventArgs e)
        {
            lock (mLock)
            {
                string text = e.Result.Text;

                Debug.WriteLine("Recognized text: " + text);
                if (text == "computer")
                {
                    mCommandTime = DateTime.Now;
                    Speak("Yes?");
                }
                else if ((DateTime.Now - mCommandTime) < COMMAND_TIMEOUT)
                {
                    mCommandTime = DateTime.Now;

                    if (mVoiceCommands.ContainsKey(text))
                    {
                        // Find and execute command
                        DeviceBase.VoiceCommand command = mVoiceCommands[text];

                        Log.Info("Running voice command: " + command.Command);
                        command.Delegate();
                        Speak("Ok");
                    }
                    else
                        Speak("I did not get that");
                }
            }
        }

        private void OnUpdateRecognizer(object sender, RecognizerUpdateReachedEventArgs e)
        {
            lock (mLock)
            {
                foreach (var grammar in mGrammarQueue)
                {
                    mRecognizer.LoadGrammarAsync(grammar);
                }
                mGrammarQueue.Clear();
            }
        }

        private void OnRecognizeCompleted(object sender, RecognizeCompletedEventArgs e)
        {
            Log.Warning("Recognize completed. Retrying");
            //mRecognizer.RecognizeAsync(RecognizeMode.Multiple);
        }

        private float Levenshtein(string s, string t)
        {
            int n = s.Length;
            int m = t.Length;
            int[,] d = new int[n + 1, m + 1];

            // Step 1
            if (n == 0)
            {
                return 0.0f;
            }

            if (m == 0)
            {
                return 0.0f;
            }

            // Step 2
            for (int i = 0; i <= n; d[i, 0] = i++)
            {
            }

            for (int j = 0; j <= m; d[0, j] = j++)
            {
            }

            // Step 3
            for (int i = 1; i <= n; i++)
            {
                //Step 4
                for (int j = 1; j <= m; j++)
                {
                    // Step 5
                    int cost = (t[j - 1] == s[i - 1]) ? 0 : 1;

                    // Step 6
                    d[i, j] = Math.Min(
                        Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost);
                }
            }

            // Step 7

            int maxLen = Math.Max(n, m);

            return (float)(maxLen - d[n, m]) / (float)maxLen;
        }

        private struct SpeechMapping
        {
            public delegate void Callback();

            public SpeechMapping(string key, Callback action)
            {
                Key = key;
                Action = action;
            }

            public string Key;
            public Callback Action;
        }

        [ServicePutContract("speak?{text}")]
        public void OnSpeakRequest(string text)
        {
            Speak(text);
        }

        [ServicePutContract("recognize?{text}")]
        public void OnRecognizeRequest(string text)
        {
            Log.Debug("Trying to recognize: " + text);

            // Try to find a high match with Levenchtein
            float bestResult = 0.0f;
            Module.DeviceBase.VoiceCommand bestCommand;

            foreach (var command in mVoiceCommands)
            {
                float result = Levenshtein(command.Key, text);

                if (result > bestResult)
                {
                    bestCommand = command.Value;
                    bestResult = result;
                }
            }

            if (bestResult > 0.72)
            {
                Log.Debug("Matched to: " + bestCommand.Command + ", confidence: " + bestResult);

                Speak("Ok");

                // Exectue delgate
                bestCommand.Delegate();
            }
            else
            {
                Log.Debug("No match for '" + text + "'. Highest was: " + bestCommand.Command + ", confidence: " + bestResult);
                Speak("I didn't get that");
            }
        }

        public void Speak(string text)
        {
            mVoice.Speak(text, SpeechVoiceSpeakFlags.SVSFlagsAsync);
        }
    }
}
