using SlimFaas;

namespace SlimFaas.Tests
{
    public class SendClientTests
    {
        /// <summary>
        /// Cas 1 : L’URL ne contient pas {pod_ip}, proxy = null.
        /// On utilise le fallback => on remplace {function_name}, {namespace}.
        /// </summary>
        [Fact]
        public async Task ComputeTargetUrlAsync_NoPodIp_ProxyNull_ReturnsFallback()
        {
            // Arrange
            string functionUrl = "http://my-service/{function_name}/{namespace}";
            string functionName = "hello-world";
            string namespaceSlimFaas = "default";
            string customRequestPath = "/path";
            string customRequestQuery = "?param=1";

            // Act
            string result = await SendClient.ComputeTargetUrlAsync(
                functionUrl,
                functionName,
                customRequestPath,
                customRequestQuery,
                namespaceSlimFaas,
                null
            );

            // Assert
            // => "http://my-service/hello-world/default/path?param=1"
            Assert.Equal("http://my-service/hello-world/default/path?param=1", result);
        }

        /// <summary>
        /// Cas 2 : L’URL contient {pod_ip}, mais proxy = null
        /// => la condition "if (functionUrl.Contains(...)" ne suffit pas,
        ///    car "&& proxy != null" échoue. => fallback.
        /// </summary>
        [Fact]
        public async Task ComputeTargetUrlAsync_WithPodIpButNullProxy_UsesFallback()
        {
            // Arrange
            string functionUrl = "http://{pod_ip}/{function_name}/{namespace}";
            string functionName = "hello-world";
            string namespaceSlimFaas = "default";
            string customRequestPath = "/path";
            string customRequestQuery = "?test=1";

            // Act
            string result = await SendClient.ComputeTargetUrlAsync(
                functionUrl,
                functionName,
                customRequestPath,
                customRequestQuery,
                namespaceSlimFaas,
                null
            );

            // Assert
            // => "http://{pod_ip}/hello-world/default/path?test=1"
            Assert.Equal("http://{pod_ip}/hello-world/default/path?test=1", result);
        }

        /// <summary>
        /// Cas 3 : L’URL contient {pod_ip} et {pod_port},
        /// proxy => renvoie direct IP et 1 port => remplacements directs.
        /// </summary>
        [Fact]
        public async Task ComputeTargetUrlAsync_WithPodIpAndPorts_DirectSuccess()
        {
            // Arrange
            string functionUrl = "http://{pod_ip}:{pod_port}/api";
            var fakeProxy = new FakeProxy
            {
                DefaultIp = "1.2.3.4",
                DefaultPorts = new List<int> { 8080 }
            };

            // Act
            string result = await SendClient.ComputeTargetUrlAsync(
                functionUrl,
                customRequestFunctionName: "",
                customRequestPath: "/test",
                customRequestQuery: "?x=1",
                namespaceSlimFaas: "default",
                proxy: fakeProxy
            );

            // Assert
            // => "http://1.2.3.4:8080/api/test?x=1"
            Assert.Equal("http://1.2.3.4:8080/api/test?x=1", result);
        }

        /// <summary>
        /// Cas 4 : Le proxy renvoie d’abord des valeurs vides,
        /// puis finalement IP et ports valables => on doit boucler plusieurs fois.
        /// </summary>
        [Fact]
        public async Task ComputeTargetUrlAsync_WithPodIp_ProxyInitiallyEmptyThenSuccess()
        {
            // Arrange
            string functionUrl = "http://{pod_ip}:{pod_port}/some/{pod_port_0}";

            var fakeProxy = new FakeProxy
            {
                Iterations = new Queue<(string ip, IList<int> ports)>()
            };
            // Ajout de 2 itérations vides, puis une itération OK
            fakeProxy.Iterations.Enqueue(("", new List<int>()));                   // 1er appel
            fakeProxy.Iterations.Enqueue(("", new List<int>()));                   // 2e appel
            fakeProxy.Iterations.Enqueue(("9.9.9.9", new List<int>{ 7777, 8888 })); // 3e appel => succès

            // Act
            string result = await SendClient.ComputeTargetUrlAsync(
                functionUrl,
                customRequestFunctionName: "",
                customRequestPath: "/path",
                customRequestQuery: "?dbg=ok",
                namespaceSlimFaas: "default",
                proxy: fakeProxy
            );

            // Assert
            // => "http://9.9.9.9:7777/some/7777/path?dbg=ok"
            Assert.Equal("http://9.9.9.9:7777/some/7777/path?dbg=ok", result);
            Assert.True(fakeProxy.GetNextIpCallsCount >= 3,
                "On doit avoir appelé GetNextIP plusieurs fois avant d'obtenir un résultat valide.");
        }

        /// <summary>
        /// Cas 5 : Le proxy ne renvoie jamais d’IP ni de ports valables => on finit par throw.
        /// </summary>
        [Fact]
        public async Task ComputeTargetUrlAsync_ProxyNeverReturnsValid_Throws()
        {
            // Arrange
            string functionUrl = "http://{pod_ip}:{pod_port}/whatever";
            var fakeProxy = new FakeProxy
            {
                Iterations = new Queue<(string ip, IList<int> ports)>()
            };
            // On remplit la queue de 10 itérations vides (on pourrait en mettre +)
            for (int i = 0; i < 10; i++)
            {
                fakeProxy.Iterations.Enqueue(("", new List<int>()));
            }

            // Act + Assert
            // On s’attend à ce que la méthode lève une Exception("Not port or IP available")
            await Assert.ThrowsAsync<Exception>(async () =>
            {
                await SendClient.ComputeTargetUrlAsync(
                    functionUrl,
                    "",
                    "",
                    "",
                    "default",
                    fakeProxy
                );
            });
        }
    }
}
public class FakeProxy : IProxy
{
    // On stocke des “itérations” successives de retours.
    public Queue<(string ip, IList<int> ports)> Iterations { get; set; } = new();

    public int GetNextIpCallsCount { get; private set; }

    // Pour un usage direct (sans itérations multiples), on peut définir
    // des valeurs simples à retourner si la queue d'itérations est vide.
    public string DefaultIp { get; set; } = "";
    public IList<int>? DefaultPorts { get; set; } = null;

    public string GetNextIP()
    {
        GetNextIpCallsCount++;

        // Si on a défini une séquence dans Iterations, on ne fait qu’inspecter la première (Peek)
        // ou on la consomme (Dequeue). À vous de voir.
        if (Iterations.Count > 0)
        {
            var (ip, _) = Iterations.Peek();
            return ip;
        }

        return DefaultIp;
    }

    public IList<int>? GetPorts()
    {
        // Ex : on consomme réellement la queue ici en .Dequeue()
        // pour simuler un nouvel état à chaque appel
        if (Iterations.Count > 0)
        {
            var tuple = Iterations.Dequeue();
            return tuple.ports;
        }

        return DefaultPorts;
    }
}
