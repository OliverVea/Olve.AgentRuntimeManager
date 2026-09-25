using Microsoft.Extensions.Logging;
using Rocks;

[assembly: Rock(typeof(ILogger<>), BuildType.Make)]
