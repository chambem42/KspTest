using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;

namespace KspTest.ServePlugins
{
    /// <summary>
    /// A request in progress starts here: ksptest serve-plugin loads it into the running session, and each rebuild replaces
    /// it there. Once it settles it moves into the harness or the module it belongs to (the requests of tools/ServePlugins
    /// so far went to src/KspTest/Serve, KspTest.Far and KspTest.AtmosphereAutopilot).
    /// </summary>
    public static class ExampleCommands
    {
        [ServeCommand("example", "name=X: says hello, with the scene; a template for new requests")]
        public static IEnumerator Example(TestContext ctx, NameValueCollection q, Dictionary<string, object> answer)
        {
            answer["hello"] = q["name"] ?? "world";
            answer["scene"] = HighLogic.LoadedScene.ToString();
            yield break;
        }
    }
}
