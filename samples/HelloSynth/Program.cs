using HelloSynth;

using Loom.Core.Hosting;

#if DEBUG
// SUIsei's diagnostics endpoint, which RoseMCP's live UI tools reach the window's tree through
SUIsei.DiagnosticsEndpoint.Start();
#endif
LoomApplication.Run(new HelloSynthApp(), args);
