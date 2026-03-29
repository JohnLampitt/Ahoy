using Ahoy.Console;
using Ahoy.Simulation.Engine;
using Ahoy.WorldData;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var world  = WorldFactory.Create(new CaribbeanWorldDefinition());
var engine = SimulationEngine.BuildEngine(world);
var harness = new SimulationHarness(engine);

harness.Run();
