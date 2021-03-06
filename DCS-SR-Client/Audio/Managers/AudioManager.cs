﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Windows;
using Ciribob.DCS.SimpleRadio.Standalone.Client.DSP;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Input;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Network;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Settings;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Singletons;
using Ciribob.DCS.SimpleRadio.Standalone.Common;
using Easy.MessageHub;
using FragLabs.Audio.Codecs;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NLog;
using Application = FragLabs.Audio.Codecs.Opus.Application;

namespace Ciribob.DCS.SimpleRadio.Standalone.Client.Audio.Managers
{
    public class AudioManager
    {
        public static readonly int INPUT_SAMPLE_RATE = 16000;
        // public static readonly int OUTPUT_SAMPLE_RATE = 44100;
        public static readonly int INPUT_AUDIO_LENGTH_MS = 80;
        public static readonly int SEGMENT_FRAMES = (INPUT_SAMPLE_RATE / 1000) * INPUT_AUDIO_LENGTH_MS; //640 is 40ms as INPUT_SAMPLE_RATE / 1000 *40 = 640
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly CachedAudioEffect[] _cachedAudioEffects;

        private readonly ConcurrentDictionary<string, ClientAudioProvider> _clientsBufferedAudio =
            new ConcurrentDictionary<string, ClientAudioProvider>();

        private readonly ConcurrentDictionary<string, SRClient> _clientsList;
        private MixingSampleProvider _clientAudioMixer;

        private OpusDecoder _decoder;

        //buffer for effects
        //plays in parallel with radio output buffer
        private RadioAudioProvider[] _effectsOutputBuffer;
        private OpusEncoder _encoder;

        private readonly Queue<byte> _micInputQueue = new Queue<byte>(SEGMENT_FRAMES*3);

        private float _speakerBoost = 1.0f;
        private volatile bool _stop = true;
        private TCPVoiceHandler _tcpVoiceHandler;
        private VolumeSampleProviderWithPeak _volumeSampleProvider;

        private WaveIn _waveIn;
        private WasapiOut _waveOut;

        public short MicMax { get; set; }
        public float SpeakerMax { get; set; }

        private ClientStateSingleton _clientStateSingleton = ClientStateSingleton.Instance;
        private WasapiOut _micWaveOut;
        private BufferedWaveProvider _micWaveOutBuffer;

        public AudioManager(ConcurrentDictionary<string, SRClient> clientsList)
        {
            _clientsList = clientsList;

            _cachedAudioEffects =
                new CachedAudioEffect[Enum.GetNames(typeof(CachedAudioEffect.AudioEffectTypes)).Length];
            for (var i = 0; i < _cachedAudioEffects.Length; i++)
            {
                _cachedAudioEffects[i] = new CachedAudioEffect((CachedAudioEffect.AudioEffectTypes) i);
            }
        }

        public float MicBoost { get; set; } = 1.0f;

        public float SpeakerBoost
        {
            get { return _speakerBoost; }
            set
            {
                _speakerBoost = value;
                if (_volumeSampleProvider != null)
                {
                    _volumeSampleProvider.Volume = value;
                }
            }
        }

        public void StartEncoding(int mic, MMDevice speakers, string guid, InputDeviceManager inputManager, IPAddress ipAddress, int port, MMDevice micOutput)
        {
            _stop = false;


            try
            {
                _micInputQueue.Clear();

                InitMixers();

                InitAudioBuffers();

                //Audio manager should start / stop and cleanup based on connection successfull and disconnect
                //Should use listeners to synchronise all the state

                _waveOut = new WasapiOut(speakers, AudioClientShareMode.Shared, true, 40);

                //add final volume boost to all mixed audio
                _volumeSampleProvider = new VolumeSampleProviderWithPeak(_clientAudioMixer,(peak => SpeakerMax = peak));
                _volumeSampleProvider.Volume = SpeakerBoost;

                if (speakers.AudioClient.MixFormat.Channels == 1)
                {
                    if (_volumeSampleProvider.WaveFormat.Channels == 2)
                    {
                        _waveOut.Init(_volumeSampleProvider.ToMono());
                    }
                    else
                    {
                        //already mono
                        _waveOut.Init(_volumeSampleProvider);
                    }

                }
                else
                {
                    if (_volumeSampleProvider.WaveFormat.Channels == 1)
                    {
                        _waveOut.Init(_volumeSampleProvider.ToStereo());
                    }
                    else
                    {
                        //already stereo
                        _waveOut.Init(_volumeSampleProvider);
                    }
                }
                _waveOut.Play();

                //opus
                _encoder = OpusEncoder.Create(INPUT_SAMPLE_RATE, 1, Application.Voip);
                _encoder.ForwardErrorCorrection = false;
                _decoder = OpusDecoder.Create(INPUT_SAMPLE_RATE, 1);
                _decoder.ForwardErrorCorrection = false;

            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error starting audio Output - Quitting! " + ex.Message);

                MessageBox.Show($"Problem Initialising Audio Output! Try a different Output device and please post your client log on the forums", "Audio Output Error", MessageBoxButton.OK,
                            MessageBoxImage.Error);

                Environment.Exit(1);
            }

            if (micOutput != null) // && micOutput !=speakers
            {
                //TODO handle case when they're the same?

                try
                {
                    _micWaveOut = new WasapiOut(micOutput, AudioClientShareMode.Shared, true, 40);

                    _micWaveOutBuffer = new  BufferedWaveProvider(new WaveFormat(AudioManager.INPUT_SAMPLE_RATE, 16, 1));
                    _micWaveOutBuffer.ReadFully = true;
                    _micWaveOutBuffer.DiscardOnBufferOverflow = true;
                    
                    RadioFilter filter = new RadioFilter(_micWaveOutBuffer.ToSampleProvider());

                    if (micOutput.AudioClient.MixFormat.Channels == 1)
                    {
                        if (filter.WaveFormat.Channels == 2)
                        {
                            _micWaveOut.Init(filter.ToMono());
                        }
                        else
                        {
                            //already mono
                            _micWaveOut.Init(filter);
                        }

                    }
                    else
                    {
                        if (filter.WaveFormat.Channels == 1)
                        {
                            _micWaveOut.Init(filter.ToStereo());
                        }
                        else
                        {
                            //already stereo
                            _micWaveOut.Init(filter);
                        }
                    }

                    _micWaveOut.Play();

                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Error starting mic audio Output - Quitting! " + ex.Message);

                    MessageBox.Show(
                        $"Problem Initialising Mic Audio Output! Try a different Output device and please post your client log on the forums",
                        "Audio Output Error", MessageBoxButton.OK,
                        MessageBoxImage.Error);

                    Environment.Exit(1);
                }

            }


            try
            {
                _waveIn = new WaveIn(WaveCallbackInfo.FunctionCallback())
                {
                    BufferMilliseconds = INPUT_AUDIO_LENGTH_MS,
                    DeviceNumber = mic,
                };

                _waveIn.NumberOfBuffers = 2;
                _waveIn.DataAvailable += _waveIn_DataAvailable;
                _waveIn.WaveFormat = new WaveFormat(INPUT_SAMPLE_RATE, 16, 1);

                _tcpVoiceHandler = new TCPVoiceHandler(_clientsList, guid, ipAddress, port, _decoder, this, inputManager);
                var voiceSenderThread = new Thread(_tcpVoiceHandler.Listen);

                voiceSenderThread.Start();

                _waveIn.StartRecording();


                MessageHub.Instance.Subscribe<SRClient>(RemoveClientBuffer);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error starting audio Input - Quitting! " + ex.Message);

                MessageBox.Show($"Problem Initialising Audio Input! Try a different Input device and please post your client log on the forums", "Audio Input Error", MessageBoxButton.OK,
                            MessageBoxImage.Error);

                Environment.Exit(1);
            }
        }

        private void InitMixers()
        {
            _clientAudioMixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(INPUT_SAMPLE_RATE, 2));
            _clientAudioMixer.ReadFully = true;
        }

        private void InitAudioBuffers()
        {
            _effectsOutputBuffer = new RadioAudioProvider[_clientStateSingleton.DcsPlayerRadioInfo.radios.Length];

            for (var i = 0; i < _clientStateSingleton.DcsPlayerRadioInfo.radios.Length; i++)
            {
                _effectsOutputBuffer[i] = new RadioAudioProvider(INPUT_SAMPLE_RATE);
                _clientAudioMixer.AddMixerInput(_effectsOutputBuffer[i].VolumeSampleProvider);
            }
        }


        public void PlaySoundEffectStartReceive(int transmitOnRadio, bool encrypted, float volume)
        {
            var radioEffects = SettingsStore.Instance.UserSettings[(int) SettingType.RadioRxEffects_Start];
            if (radioEffects == "ON")
            {
                var _effectsBuffer = _effectsOutputBuffer[transmitOnRadio];

                var encyptionEffects = SettingsStore.Instance.UserSettings[(int) SettingType.RadioEncryptionEffects];
                if (encrypted && (encyptionEffects == "ON"))
                {
                    _effectsBuffer.VolumeSampleProvider.Volume = volume;
                    _effectsBuffer.AddAudioSamples(
                        _cachedAudioEffects[(int) CachedAudioEffect.AudioEffectTypes.KY_58_RX].AudioEffectBytes,
                        transmitOnRadio);
                }
                else
                {
                    _effectsBuffer.VolumeSampleProvider.Volume = volume;
                    _effectsBuffer.AddAudioSamples(
                        _cachedAudioEffects[(int) CachedAudioEffect.AudioEffectTypes.RADIO_TX].AudioEffectBytes,
                        transmitOnRadio);
                }
            }
        }

        public void PlaySoundEffectStartTransmit(int transmitOnRadio, bool encrypted, float volume)
        {
            var radioEffects = SettingsStore.Instance.UserSettings[(int) SettingType.RadioTxEffects_Start];
            if (radioEffects == "ON")
            {
                var _effectBuffer = _effectsOutputBuffer[transmitOnRadio];

                var encyptionEffects = SettingsStore.Instance.UserSettings[(int) SettingType.RadioEncryptionEffects];

                if (encrypted && (encyptionEffects == "ON"))
                {
                    _effectBuffer.VolumeSampleProvider.Volume = volume;
                    _effectBuffer.AddAudioSamples(
                        _cachedAudioEffects[(int) CachedAudioEffect.AudioEffectTypes.KY_58_TX].AudioEffectBytes,
                        transmitOnRadio);
                }
                else
                {
                    _effectBuffer.VolumeSampleProvider.Volume = volume;
                    _effectBuffer.AddAudioSamples(
                        _cachedAudioEffects[(int) CachedAudioEffect.AudioEffectTypes.RADIO_TX].AudioEffectBytes,
                        transmitOnRadio);
                }
            }
        }


        public void PlaySoundEffectEndReceive(int transmitOnRadio, float volume)
        {
            var radioEffects = SettingsStore.Instance.UserSettings[(int) SettingType.RadioRxEffects_End];
            if (radioEffects == "ON")
            {
                var _effectsBuffer = _effectsOutputBuffer[transmitOnRadio];

                _effectsBuffer.VolumeSampleProvider.Volume = volume;
                _effectsBuffer.AddAudioSamples(
                    _cachedAudioEffects[(int) CachedAudioEffect.AudioEffectTypes.RADIO_RX].AudioEffectBytes,
                    transmitOnRadio);
            }
        }

        public void PlaySoundEffectEndTransmit(int transmitOnRadio, float volume)
        {
            var radioEffects = SettingsStore.Instance.UserSettings[(int) SettingType.RadioTxEffects_End];
            if (radioEffects == "ON")
            {
                var _effectBuffer = _effectsOutputBuffer[transmitOnRadio];

                _effectBuffer.VolumeSampleProvider.Volume = volume;
                _effectBuffer.AddAudioSamples(
                    _cachedAudioEffects[(int) CachedAudioEffect.AudioEffectTypes.RADIO_RX].AudioEffectBytes,
                    transmitOnRadio);
            }
        }

         //Stopwatch _stopwatch = new Stopwatch();
        private void _waveIn_DataAvailable(object sender, WaveInEventArgs e)
        {
            // if(_stopwatch.ElapsedMilliseconds > 22)
            //Console.WriteLine($"Time: {_stopwatch.ElapsedMilliseconds} - Bytes: {e.BytesRecorded}");
            // _stopwatch.Restart();

            byte[] soundBuffer = null;
            if ((e.BytesRecorded == SEGMENT_FRAMES) && (_micInputQueue.Count == 0))
            {
                //perfect!
                soundBuffer = new byte[e.BytesRecorded];
                Buffer.BlockCopy(e.Buffer, 0, soundBuffer, 0, e.BytesRecorded);
            }
            else
            {
                for (var i = 0; i < e.BytesRecorded; i++)
                {
                    _micInputQueue.Enqueue(e.Buffer[i]);
                }
            }

            //read out the queue
            while ((soundBuffer != null) || (_micInputQueue.Count >= SEGMENT_FRAMES))
            {
                //null sound buffer so read from the queue
                if (soundBuffer == null)
                {
                    soundBuffer = new byte[SEGMENT_FRAMES];

                    for (var i = 0; i < SEGMENT_FRAMES; i++)
                    {
                        soundBuffer[i] = _micInputQueue.Dequeue();
                    }
                }

                short max = 0;
                for (var n = 0; n < soundBuffer.Length; n += 2)
                {
                    var sample = (short) ((soundBuffer[n + 1] << 8) | soundBuffer[n + 0]);

                    // n.b. no clipping test going on here // FROM NAUDIO SOURCE !
                    sample = (short) (sample*MicBoost);

                    //determine peak
                    if (sample > max)
                        max = sample;

                    //convert back
                    soundBuffer[n] = (byte) (sample & 0xFF);
                    soundBuffer[n + 1] = (byte) (sample >> 8);   
                }

                MicMax = max;

                try
                {
                    //encode as opus bytes
                    int len;
                    var buff = _encoder.Encode(soundBuffer, soundBuffer.Length, out len);

                    if ((_tcpVoiceHandler != null) && (buff != null) && (len > 0))
                    {
                        //create copy with small buffer
                        var encoded = new byte[len];

                        Buffer.BlockCopy(buff, 0, encoded, 0, len);

                        // Console.WriteLine("Sending: " + e.BytesRecorded);
                        if (_tcpVoiceHandler.Send(encoded, len))
                        {
                            //send audio so play over local too
                            if (_micWaveOutBuffer != null)
                            {
                                _micWaveOutBuffer.AddSamples(soundBuffer, 0, soundBuffer.Length);
                            }
                           
                        }
                    }
                    else
                    {
                        Logger.Error($"Invalid Bytes for Encoding - {e.BytesRecorded} should be {SEGMENT_FRAMES} ");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Error encoding Opus! " + ex.Message);
                }

                soundBuffer = null;
            }
        }

        public void StopEncoding()
        {
            if (_clientAudioMixer != null)
            {
                _effectsOutputBuffer = null;

                _volumeSampleProvider = null;
                _clientAudioMixer.RemoveAllMixerInputs();
                _clientAudioMixer = null;
            }

            _clientsBufferedAudio.Clear();

            if (_waveIn != null)
            {
                _waveIn.StopRecording();
                _waveIn.Dispose();
                _waveIn = null;
            }

            if (_waveOut != null)
            {
                _waveOut.Stop();
                _waveOut.Dispose();
                _waveOut = null;
            }

            if (_micWaveOut != null)
            {
                _micWaveOut.Stop();
                _micWaveOut.Dispose();
                _micWaveOut = null;
            }

            if (_encoder != null)
            {
                _encoder.Dispose();
                _encoder = null;
            }

            if (_decoder != null)
            {
                _decoder.Dispose();
                _decoder = null;
            }
            if (_tcpVoiceHandler != null)
            {
                _tcpVoiceHandler.RequestStop();
                _tcpVoiceHandler = null;
            }

            _stop = true;

            SpeakerMax = 0;
            MicMax = 0;

            MessageHub.Instance.ClearSubscriptions();
        }

        public void AddClientAudio(ClientAudio audio)
        {
            //sort out effects!

            //16bit PCM Audio
            //TODO: Clean  - remove if we havent received audio in a while?
            // If we have recieved audio, create a new buffered audio and read it
            ClientAudioProvider client = null;
            if (_clientsBufferedAudio.ContainsKey(audio.ClientGuid))
            {
                client = _clientsBufferedAudio[audio.ClientGuid];
            }
            else
            {
                client = new ClientAudioProvider();
                _clientsBufferedAudio[audio.ClientGuid] = client;

                _clientAudioMixer.AddMixerInput(client.SampleProvider);
            }

            client.AddClientAudioSamples(audio);
        }

        private void RemoveClientBuffer(SRClient srClient)
        {
            ClientAudioProvider clientAudio = null;
            _clientsBufferedAudio.TryRemove(srClient.ClientGuid, out clientAudio);

            if (clientAudio != null)
            {
                try
                {
                    _clientAudioMixer.RemoveMixerInput(clientAudio.SampleProvider);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Error removing client input");
                }
            }
        }
    }
}