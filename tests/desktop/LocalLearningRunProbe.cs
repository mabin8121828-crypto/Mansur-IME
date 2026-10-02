// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
// Production components, fixed confirmed text, own private desktop and clipboard sink.
// This is not evidence of input into user applications or of a listener hearing speech.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class LocalLearningRunProbe
{
    private const BindingFlags Inside=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
    private const BindingFlags Static=BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public;
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern bool GetUserObjectInformation(IntPtr handle,int index,StringBuilder value,int size,out int required);
    [DllImport("user32.dll")] private static extern IntPtr GetFocus();
    private static readonly JavaScriptSerializer Serializer=new JavaScriptSerializer();
    private static Type TypeIn(Assembly a,string name){return a.GetType("Mansur.Next.Desktop."+name,true);}
    private static object Call(object value,string name,params object[] args){return value.GetType().GetMethod(name,Inside).Invoke(value,args);}
    private static object Make(Type type,params object[] args){return Activator.CreateInstance(type,Inside,null,args,null);}
    private static void Check(bool ok,string code){if(!ok)throw new InvalidOperationException(code);}
    private static void Pump(){Application.DoEvents();Thread.Sleep(15);}
    [STAThread]
    private static int Main(string[] args)
    {
        if(args.Length!=4)return 2;
        string evidence=args[3];Directory.CreateDirectory(evidence);
        Console.SetOut(new StreamWriter(Path.Combine(evidence,"run.log"),false,new UTF8Encoding(false)){AutoFlush=true});
        var name=new StringBuilder(256);int required;
        if(!GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()),2,name,name.Capacity*2,out required)||
            !name.ToString().StartsWith("MansurFocusProbe",StringComparison.Ordinal))return 2;
        IDisposable worker=null,listener=null,player=null;
        var events=new Queue<Dictionary<string,object>>();object gate=new object();string failed=null;
        try
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            Assembly a=Assembly.LoadFrom(args[0]);
            var source=Serializer.Deserialize<Dictionary<string,object>>(File.ReadAllText(args[2],Encoding.UTF8));
            Check(Object.Equals(source["core_confirmed"],true)&&Object.Equals(source["chinese"],"你好")&&Object.Equals(source["english"],"Hello"),"fixed_core_source");
            object config=TypeIn(a,"Configuration").GetMethod("Load",Static).Invoke(null,new object[]{args[1]});
            Check(Object.Equals(config.GetType().GetField("TranslationProvider",Inside).GetValue(config),"local"),"local_only");
            string package=Directory.GetParent(Path.GetDirectoryName(args[0])).FullName;
            config.GetType().GetField("Worker",Inside).SetValue(config,Path.Combine(package,"learning","worker.py"));
            string copied=null;
            using(var host=new Form{Size=new Size(900,600),Text="Mansur fixed-input fixture"})
            using(var editor=new TextBox{Multiline=true,Dock=DockStyle.Fill})
            using(var popup=(Form)Make(TypeIn(a,"FloatingForm"),(Action<string>)(text=>copied=text)))
            {
                host.Controls.Add(editor);host.Show();host.Activate();editor.Focus();Pump();
                int lost=0;editor.LostFocus+=delegate{lost++;};
                var textView=(TextBox)popup.GetType().GetProperty("TextView",Inside).GetValue(popup);
                player=(IDisposable)Make(TypeIn(a,"PcmPlayer"),(Action<long,string>)((id,code)=>{lock(gate)failed=code;}));
                var startup=Stopwatch.StartNew();
                worker=(IDisposable)Make(TypeIn(a,"WorkerProcess"),config,
                    (Action<Dictionary<string,object>>)(evt=>{lock(gate)events.Enqueue(evt);}),
                    (Action<string>)(code=>{lock(gate)failed=code;}));
                Console.WriteLine("OWN_WORKER_STARTED");
                bool ready=false;
                while(startup.Elapsed.TotalSeconds<65&&!ready)
                {
                    Dictionary<string,object> evt=null;
                    lock(gate){if(events.Count>0)evt=events.Dequeue();Check(failed==null,"worker_start_failed");}
                    if(evt!=null){if(Object.Equals(evt["event"],"ready"))ready=true;else Check(!Object.Equals(evt["event"],"error"),"model_start_error");}
                    Pump();
                }
                Check(ready,"worker_ready_timeout");long voiceReadyMs=startup.ElapsedMilliseconds;Console.WriteLine("VOICE_READY startup_ms="+voiceReadyMs);
                // The production listener uses a unique name, with the current user's ACL.
                string pipe="MansurNext.FixedRun."+Guid.NewGuid().ToString("N");
                object request=null;var requestGate=new object();
                listener=(IDisposable)Make(TypeIn(a,"PipeListener"),WindowsIdentity.GetCurrent().User,
                    (Action<string>)(line=>{lock(requestGate)request=TypeIn(a,"LearningRequest").GetMethod("Parse",Static).Invoke(null,new object[]{line});}),pipe);
                var rows=new List<object>();long id=0;
                foreach(string key in new[]{"chinese","english"})
                {
                    id++;string input=(string)source[key];editor.Select(editor.TextLength,0);editor.SelectedText=input;
                    var clock=Stopwatch.StartNew();long translatedMs=-1,firstAudioMs=-1;string english="";bool done=false,shown=false;
                    int rate=0,chunks=0;var pcm=new MemoryStream();
                    Call(player,"Reset",id);Call(popup,"Present","","正在准备英文…",(Rectangle?)new Rectangle(100,100,0,24));Pump();
                    Check(GetFocus()==editor.Handle&&lost==0,"waiting_stole_focus");
                    string message=Serializer.Serialize(new{op="learn",context="fixed-"+key,sender="fixed-fixture",sent_ticks=Stopwatch.GetTimestamp(),sent_sequence=id,revision=1,text=input,voice="af_heart",speed=1.0});
                    lock(requestGate)request=null;
                    using(var client=new NamedPipeClientStream(".",pipe,PipeDirection.InOut))
                    {client.Connect(2500);byte[] payload=Encoding.UTF8.GetBytes(message+"\n");client.Write(payload,0,payload.Length);client.Flush();Check(client.ReadByte()==6,"pipe_ack");}
                    lock(requestGate){Check(request!=null,"pipe_parse");request.GetType().GetField("Id",Inside).SetValue(request,id);Check((bool)Call(worker,"Send",Call(request,"WorkerCommand")),"worker_send");}
                    Console.WriteLine("FIXED_REQUEST "+key);
                    while(clock.Elapsed.TotalSeconds<100&&!(done&&(bool)Call(player,"IsDrained",id)))
                    {
                        Dictionary<string,object> evt=null;
                        lock(gate){Check(failed==null,"worker_or_audio_failed");if(events.Count>0)evt=events.Dequeue();}
                        if(evt!=null)
                        {
                            string kind=(string)evt["event"];
                            if(kind=="translation")
                            {english=(string)evt["text"];translatedMs=clock.ElapsedMilliseconds;Call(popup,"Present",english,"",(Rectangle?)new Rectangle(100,100,0,24));Pump();shown=popup.Visible&&textView.Text==english;Check(shown&&GetFocus()==editor.Handle&&lost==0,"result_or_focus_failed");}
                            else if(kind=="audio")
                            {byte[] bytes=Convert.FromBase64String((string)evt["pcm_s16le"]);rate=Convert.ToInt32(evt["sample_rate"]);Check(Convert.ToInt32(evt["chunk_index"])==chunks++,"audio_chunk_order");pcm.Write(bytes,0,bytes.Length);if(firstAudioMs<0)firstAudioMs=clock.ElapsedMilliseconds;Check((bool)Call(player,"Enqueue",id,bytes,rate),"audio_enqueue");}
                            else if(kind=="done")done=true;
                            else if(kind=="error")throw new InvalidOperationException("model_request_error_"+(string)evt["code"]);
                        }
                        Pump();
                    }
                    Check(done&&shown&&pcm.Length>0&&(bool)Call(player,"IsDrained",id),"learning_or_playback_timeout");
                    long drainedMs=clock.ElapsedMilliseconds;
                    if(key=="english")Check(english=="Hello","English_not_preserved");
                    byte[] audio=pcm.ToArray();double energy=0;
                    for(int j=0;j<audio.Length;j+=2){short sample=(short)(audio[j]|audio[j+1]<<8);energy+=(double)sample*sample;}
                    double rms=Math.Sqrt(energy/(audio.Length/2));Check(rms>1,"silent_audio");
                    WriteWave(Path.Combine(evidence,key+".wav"),audio,rate);
                    using(var bitmap=new Bitmap(popup.Width,popup.Height)){popup.DrawToBitmap(bitmap,new Rectangle(Point.Empty,popup.Size));bitmap.Save(Path.Combine(evidence,key+"-popup.png"));}
                    // Explicit copy goes to a test sink; never touch or back up the real clipboard.
                    Call(popup,"CopyAll");Check(copied==english,"whole_copy");
                    var activation=Message.Create(textView.Handle,0x0021,popup.Handle,IntPtr.Zero);object[] activationArgs={activation};
                    textView.GetType().GetMethod("WndProc",Inside).Invoke(textView,activationArgs);popup.Activate();textView.Focus();Pump();
                    textView.Select(0,Math.Min(5,english.Length));Call(textView,"HandleCopyKey",Keys.Control|Keys.C);Check(copied==english.Substring(0,Math.Min(5,english.Length)),"selection_copy");
                    host.Activate();editor.Focus();Pump();lost=0;
                    object lifetime=Make(TypeIn(a,"LearningWindowLifetime"));var hideClock=Stopwatch.StartNew();bool hide=false;
                    while(hideClock.Elapsed.TotalSeconds<5&&!hide){hide=(bool)Call(lifetime,"ShouldHide",true,(bool)Call(player,"IsDrained",id),false,Stopwatch.GetTimestamp());Pump();}
                    Check(hide,"auto_hide_timeout");popup.Hide();Pump();Check(!popup.Visible&&GetFocus()==editor.Handle,"hide_stole_focus");
                    rows.Add(new{input_kind=key,fixed_input=input,english=english,translation_ms=translatedMs,first_audio_ms=firstAudioMs,playback_drained_ms=drainedMs,
                        audio_seconds=(double)audio.Length/rate/2,audio_rms=rms,audio_chunks=chunks,result_displayed=shown,no_automatic_focus_loss=true,whole_copy=true,selection_copy=true,auto_hide_ms=hideClock.ElapsedMilliseconds});
                    Console.WriteLine("CASE_PASS "+key+" translation_ms="+translatedMs+" first_audio_ms="+firstAudioMs);
                    pcm.Dispose();
                }
                Check(editor.Text=="你好Hello","fixture_text_changed");
                File.WriteAllText(Path.Combine(evidence,"result.json"),Serializer.Serialize(new{status="PASS",voice_ready_ms=voiceReadyMs,cases=rows,
                    scope="Fixed production core confirmations, own private editor (programmatic insertion, not a real TSF host), private production IPC listener, real local worker, production popup and waveOut playback. Copy uses fake sink. No user apps, actual clipboard, API or input history accessed."}),new UTF8Encoding(false));
                Console.WriteLine("LOCAL_LEARNING_RUN_PASS");return 0;
            }
        }
        catch(Exception error)
        {while(error is TargetInvocationException&&error.InnerException!=null)error=error.InnerException;Console.WriteLine("FAILED "+error.GetType().Name+" "+(error is InvalidOperationException?error.Message:"fixed_fixture_error"));return 1;}
        finally {if(listener!=null)listener.Dispose();if(worker!=null)worker.Dispose();if(player!=null)player.Dispose();}
    }
    private static void WriteWave(string path,byte[] pcm,int rate)
    {using(var w=new BinaryWriter(File.Create(path))){w.Write(Encoding.ASCII.GetBytes("RIFF"));w.Write(36+pcm.Length);w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));w.Write(16);w.Write((short)1);w.Write((short)1);w.Write(rate);w.Write(rate*2);w.Write((short)2);w.Write((short)16);w.Write(Encoding.ASCII.GetBytes("data"));w.Write(pcm.Length);w.Write(pcm);}}
}
