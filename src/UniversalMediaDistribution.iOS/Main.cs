using UIKit;

namespace UniversalMediaDistribution.iOS;

public static class Application
{
    public static void Main(string[] args) =>
        UIApplication.Main(args, null, typeof(AppDelegate));
}
