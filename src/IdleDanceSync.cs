using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HelltakerAILab
{
    internal sealed class IdleDanceSync
    {
        JObject config;
        readonly Dictionary<string, Sprite[]> frames = new Dictionary<string, Sprite[]>();
        AudioClip lastTrack;
        bool initialized;
        double phase, sampledTime, sourceTime, bpm=150, offset;
        int index;
        string track="", clipName="";
        bool playing, held, fallback, active;

        public void Configure(JObject settings)
        {
            config=settings; frames.Clear(); initialized=false; lastTrack=null;
            phase=0; sampledTime=0; sourceTime=0; index=0; active=false;
            if (config==null) return;
            var sprites=Resources.FindObjectsOfTypeAll<Sprite>().Where(s=>s).GroupBy(s=>s.name).ToDictionary(g=>g.Key,g=>g.First());
            foreach (var pair in ((JObject)config["clips"]).Properties())
            {
                var names=((JArray)pair.Value).Values<string>().ToArray();
                if (names.Length>0 && names.All(n=>sprites.ContainsKey(n)))
                    frames[pair.Name]=names.Select(n=>sprites[n]).ToArray();
            }
        }

        public void Tick(SpriteRenderer renderer, string currentClip)
        {
            active=false; clipName=currentClip;
            if (config==null) return;
            var source=Manager.instance ? Manager.instance.musicSource : null;
            var audio=source ? source.clip : null;
            track=audio ? audio.name : "";
            var profile=(config["profiles"] as JObject)?[track] as JObject;
            fallback=profile==null;
            bpm=Math.Max(1,(double?)profile?["bpm"] ?? (double?)config["fallback_bpm"] ?? 150);
            offset=(double?)profile?["offset_seconds"] ?? 0;
            var beats=Math.Max(1,(double?)config["beats_per_cycle"] ?? 2);
            playing=source && audio && source.isPlaying;
            sourceTime=audio ? (double)source.timeSamples/audio.frequency : 0;
            held=Time.timeScale==0 || (audio && !playing);
            var changed=audio!=lastTrack;
            if (!initialized || changed || !held)
            {
                if (audio) sampledTime=sourceTime;
                else if (Time.timeScale>0) sampledTime+=Time.unscaledDeltaTime;
                var cycles=(sampledTime-offset)*bpm/(60*beats);
                phase=cycles-Math.Floor(cycles);
                initialized=true; lastTrack=audio;
            }
            if (renderer && frames.TryGetValue(currentClip,out var sequence))
            {
                index=(int)Math.Floor(phase*sequence.Length+.5)%sequence.Length;
                if (sequence[index]) { renderer.sprite=sequence[index]; active=true; }
            }
        }

        public JObject Status() => new JObject {
            ["enabled"]=config!=null,["active_idle"]=active,["track"]=track,["clip"]=clipName,
            ["bpm"]=bpm,["offset_seconds"]=offset,["phase"]=phase,["frame_index"]=index,
            ["sampled_time_seconds"]=sampledTime,["source_time_seconds"]=sourceTime,
            ["playing"]=playing,["held"]=held,["fallback_profile"]=fallback
        };
    }
}
