using System.Windows;
using MaxRev.Gdal.Core;
using MyGIS.Services;
using OSGeo.GDAL;
using System.Text;

namespace MyGIS
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
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
        }

        protected override void OnExit(ExitEventArgs e)
        {
            base.OnExit(e);
            GdalDatasetManager.DisposeAll();
        }
    }
}
