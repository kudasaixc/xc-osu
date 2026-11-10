using System;
using osu.Web;
static void Check(bool value, string name) { if (!value) throw new Exception(name); }
Check(!StutterCapturePolicy.ShouldCapture(16.67,16.67,0,0), "smooth 60Hz must not capture, regardless of update CPU work");
Check(!StutterCapturePolicy.ShouldCapture(8.33,8.33,0,0), "smooth 120Hz");
Check(StutterCapturePolicy.ShouldCapture(33.33,16.67,0,0), "missed 60Hz refresh");
Check(StutterCapturePolicy.ShouldCapture(16.67,8.33,0,0), "missed 120Hz refresh");
Check(!StutterCapturePolicy.ShouldCapture(16.67,16.67,1,0), "one repeated frame is insufficient");
Check(StutterCapturePolicy.ShouldCapture(16.67,16.67,2,0), "capture while update remains stalled");
Check(StutterCapturePolicy.ShouldCapture(16.67,16.67,0,6), "capture update recovery");
Check(!StutterCapturePolicy.ShouldCapture(1500,16.67,2,6), "ignore background resumption");
Check(StutterCapturePolicy.IsSevere(200,16.67,0,0), "major deadline miss bypasses cooldown");
Check(StutterCapturePolicy.IsSevere(16.67,16.67,0,6), "major presentation stall bypasses cooldown");
Check(!StutterCapturePolicy.IsSevere(33.33,16.67,0,0), "ordinary misses remain sampled");
Console.WriteLine("PASS: 60/120Hz deadlines, repeat stalls, resumption, severe-stall priority");
