using System.Windows;
using MaxRev.Gdal.Core;
using GeoVision.Services;
using OSGeo.GDAL;
using System.Text;

namespace GeoVision
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            GdalBase.ConfigureAll();
            Gdal.SetConfigOption("GDAL_NUM_THREADS", "ALL_CPUS");
            Gdal.SetConfigOption("GDAL_CACHEMAX", "1024");
            Gdal.SetConfigOption("GDAL_TIFF_OVR_BLOCKSIZE", "512");
            Gdal.SetConfigOption("COMPRESS_OVERVIEW", "LZW");
            Gdal.SetConfigOption("PREDICTOR_OVERVIEW", "2");
            Gdal.SetConfigOption("INTERLEAVE_OVERVIEW", "BAND");
            Gdal.SetConfigOption("BIGTIFF_OVERVIEW", "IF_SAFER");
            Gdal.SetConfigOption("SPARSE_OK_OVERVIEW", "ON");

            if (OverviewBuildWorker.IsWorkerCommand(e.Args))
            {
                int exitCode = OverviewBuildWorker.Run(e.Args);
                Shutdown(exitCode);
                return;
            }

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            GdalDatasetManager.DisposeAll();
            base.OnExit(e);
        }
    }
}
