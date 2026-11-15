using VideoTrimmer;
using System.Diagnostics;
using System.Text.Json;
var root = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/hdr-export-check");
Directory.CreateDirectory(root);
var runner = new FfmpegRunner();
if (runner.Ffmpeg is null || runner.Ffprobe is null) throw new Exception("Put the packaged FFmpeg tools on PATH before running this check.");
var ff = runner.Ffmpeg!;
var source = Path.Combine(root, "hour-hdr.mkv");
if (!File.Exists(source)) {
 Console.WriteLine("Generating one-hour PQ/5.1 fixture...");
 await runner.RunAsync(ff, ["-v","error","-y","-f","lavfi","-i","testsrc2=size=96x64:rate=1:duration=3605","-f","lavfi","-i","aevalsrc=0|0|0.15*sin(440*2*PI*t)|0|0|0:s=8000:d=3605:c=5.1","-c:v","libx265","-preset","ultrafast","-pix_fmt","yuv420p10le","-color_trc","smpte2084","-color_primaries","bt2020","-colorspace","bt2020nc","-x265-params","log-level=error:keyint=10:scenecut=0:master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,1):max-cll=1000,400","-c:a","flac","-bsf:v","hevc_metadata=colour_primaries=9:transfer_characteristics=16:matrix_coefficients=9",source]);
}
var media = await MediaProbe.ProbeAsync(runner, source, default);
if (!media.Color.IsHdr || media.Color.Transfer != "smpte2084") throw new Exception("HDR fixture detection failed");
var options = ExactExportOptions.Default with { Color = media.Color };
var results = new List<object>();
async Task Check(string name, ExportPlan plan, bool audio = true, double expected = 3) {
 var watch = Stopwatch.StartNew();
 if (plan.WorkDirectory is {} work) { Directory.CreateDirectory(work); File.WriteAllText(plan.ListFile!,plan.ListText); }
 foreach (var step in plan.Steps) await runner.RunAsync(ff, step.Arguments);
 watch.Stop();
 foreach(var output in plan.Outputs) {
  var json = await runner.RunAsync(runner.Ffprobe!, ["-v","error","-show_streams","-show_format","-of","json",output]);
  using var doc=JsonDocument.Parse(json);
  var streams=doc.RootElement.GetProperty("streams").EnumerateArray().ToArray();
  var video=streams.Single(s=>s.GetProperty("codec_type").GetString()=="video");
  if (video.GetProperty("pix_fmt").GetString()!="yuv420p") throw new Exception(name+" pixel format");
  if (options.Color.IsHdr && video.GetProperty("color_transfer").GetString()!="bt709") throw new Exception(name+" SDR tagging");
  if(video.TryGetProperty("side_data_list",out var side) && (side.ToString().Contains("Mastering display") || side.ToString().Contains("Content light") || side.ToString().Contains("DOVI"))) throw new Exception(name+" stale HDR metadata");
  var frames=await runner.RunAsync(runner.Ffprobe!,["-v","error","-select_streams","v:0","-read_intervals","%+#8","-show_frames","-of","json",output]);
  if(frames.Contains("Mastering display") || frames.Contains("Content light") || frames.Contains("DOVI")) throw new Exception(name+" decoded frames retain HDR metadata");
  var aud=streams.Where(s=>s.GetProperty("codec_type").GetString()=="audio").ToArray();
  if(audio && (aud.Length!=1 || aud[0].GetProperty("channels").GetInt32()!=2 || aud[0].GetProperty("codec_name").GetString()!="aac")) throw new Exception(name+" stereo AAC");
  if(!audio && aud.Length!=0) throw new Exception(name+" silent export added audio");
  var length=double.Parse(doc.RootElement.GetProperty("format").GetProperty("duration").GetString()!,System.Globalization.CultureInfo.InvariantCulture);
  if(Math.Abs(length-expected)>0.35) throw new Exception(name+" duration "+length);
  if(audio) {
   var pcm=await runner.RunBinaryAsync(ff,["-v","error","-i",output,"-map","0:a:0","-f","f32le","-acodec","pcm_f32le","-"]);
   var energy=new double[2];
   for(int i=0;i+7<pcm.Length;i+=8) for(int c=0;c<2;c++){var v=BitConverter.ToSingle(pcm,i+c*4);energy[c]+=v*v;}
   if(energy.Any(v=>v<1)) throw new Exception(name+" center dialogue missing from stereo");
  }
  File.WriteAllText(output+".probe.json",json);
 }
 results.Add(new {name,seconds=watch.Elapsed.TotalSeconds,outputs=plan.Outputs});
 Console.WriteLine(name+" passed in "+watch.Elapsed.TotalSeconds.ToString("0.00")+"s");
}
await Check("late CPU HDR",ExportService.Plan(source,Path.Combine(root,"late-cpu.mp4"),[new(3600,3603)],1,true,false,options));
var gpu=await HdrExport.SupportsPlaceboAsync(runner,default);
Console.WriteLine("libplacebo driver available: "+gpu);
if(gpu) await Check("late libplacebo HDR",ExportService.Plan(source,Path.Combine(root,"late-gpu.mp4"),[new(3600,3603)],1,true,false,options with {UseLibplacebo=true}));
await Check("HDR concat",ExportService.Plan(source,Path.Combine(root,"concat.mp4"),[new(3500,3501),new(3600,3602)],1,true,false,options));
await Check("HDR separate",ExportService.PlanSeparate(source,Path.Combine(root,"separate.mp4"),[new(3500,3503),new(3600,3603)],1,false,options));
var silent=Path.Combine(root,"silent-hlg.mkv");
await runner.RunAsync(ff,["-v","error","-y","-ss","3600","-i",source,"-t","4","-an","-c:v","copy","-bsf:v","hevc_metadata=colour_primaries=9:transfer_characteristics=18:matrix_coefficients=9",silent]);
var hlg=await MediaProbe.ProbeAsync(runner,silent,default); if(hlg.Color.Transfer!="arib-std-b67") throw new Exception("HLG fixture tags missing");
await Check("silent HLG",ExportService.Plan(silent,Path.Combine(root,"hlg.mp4"),[new(0,3)],1,false,false,options with {Color=hlg.Color}),false);
var sdr=Path.Combine(root,"sdr.mkv");
await runner.RunAsync(ff,["-v","error","-y","-f","lavfi","-i","testsrc2=size=96x64:rate=30:duration=4","-f","lavfi","-i","aevalsrc=0|0|0.15*sin(440*2*PI*t)|0|0|0:s=8000:d=4:c=5.1","-c:v","libx264","-g","30","-pix_fmt","yuv420p","-color_trc","bt709","-color_primaries","bt709","-colorspace","bt709","-c:a","flac",sdr]);
options=ExactExportOptions.Default;
await Check("fast video stereo",ExportService.Plan(sdr,Path.Combine(root,"fast.mp4"),[new(0,3)],1,true,false,options));
File.WriteAllText(Path.Combine(root,"results.json"),JsonSerializer.Serialize(new {gpu,results},new JsonSerializerOptions {WriteIndented=true}));



