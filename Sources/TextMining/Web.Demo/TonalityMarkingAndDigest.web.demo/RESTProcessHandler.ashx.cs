using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Text;
using System.Web;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Xsl;

using Newtonsoft.Json;
using captcha;
using Digest;
using OpinionMining;
using TextMining.Core;
using TonalityMarking;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Threading;

#if WITH_OM_TM
using Lingvistics.Client;
#endif

namespace TonalityMarkingAndDigest.web.demo
{
    /// <summary>
    /// 
    /// </summary>
    internal static class Config
    {
#if WITH_OM_TM
        public static readonly string LINGUISTICS_SERVER_URL                  = ConfigurationManager.AppSettings[ "LINGUISTICS_SERVER_URL" ];
        public static readonly int    LINGUISTICS_SERVER_TIMEOUT_IN_SECONDS   = ConfigurationManager.AppSettings[ "LINGUISTICS_SERVER_TIMEOUT_IN_SECONDS" ].ToInt32();
        public static readonly string LINGUISTICS_SERVER_EXE_FILE_LOCATION    = ConfigurationManager.AppSettings[ "LINGUISTICS_SERVER_EXE_FILE_LOCATION" ];
        public static readonly bool   LINGUISTICS_SERVER_EXE_CREATE_NO_WINDOW = ConfigurationManager.AppSettings[ "LINGUISTICS_SERVER_EXE_CREATE_NO_WINDOW" ].TryToBool( false );

        public static ProcessStartInfo CreateLinguisticsServerProcessStartInfo()
        {
            var startInfo = new ProcessStartInfo( LINGUISTICS_SERVER_EXE_FILE_LOCATION, "-console" );
            if ( LINGUISTICS_SERVER_EXE_CREATE_NO_WINDOW )
            {
                startInfo.WindowStyle     = ProcessWindowStyle.Hidden;
                startInfo.CreateNoWindow  = false;
                startInfo.UseShellExecute = false;
            }
            return (startInfo);
        }
#else
        public static readonly string TONALITY_MARKING_ENDPOINT_CONFIGURATION_NAME = ConfigurationManager.AppSettings[ "TONALITY_MARKING_ENDPOINT_CONFIGURATION_NAME" ];
        public static readonly string DIGEST_ENDPOINT_CONFIGURATION_NAME           = ConfigurationManager.AppSettings[ "DIGEST_ENDPOINT_CONFIGURATION_NAME" ];
#endif
        public static readonly int MAX_INPUTTEXT_LENGTH                = ConfigurationManager.AppSettings[ "MAX_INPUTTEXT_LENGTH"                ].ToInt32();
        public static readonly int SAME_IP_INTERVAL_REQUEST_IN_SECONDS = ConfigurationManager.AppSettings[ "SAME_IP_INTERVAL_REQUEST_IN_SECONDS" ].ToInt32();
        public static readonly int SAME_IP_MAX_REQUEST_IN_INTERVAL     = ConfigurationManager.AppSettings[ "SAME_IP_MAX_REQUEST_IN_INTERVAL"     ].ToInt32();        
        public static readonly int SAME_IP_BANNED_INTERVAL_IN_SECONDS  = ConfigurationManager.AppSettings[ "SAME_IP_BANNED_INTERVAL_IN_SECONDS"  ].ToInt32();
    }
}

namespace TonalityMarkingAndDigest.web.demo
{
    /// <summary>
    /// Summary description for RESTProcessHandler
    /// </summary>
    public sealed class RESTProcessHandler : IHttpHandler
    {
        /// <summary>
        /// 
        /// </summary>
        internal sealed class Result
        {
            public Result( Exception ex ) 
            {
                ExceptionMessage = ex.ToString();
            }
            public Result( string html )
            {
                Html = html;
            }

            [JsonProperty(PropertyName="err")]
            public string ExceptionMessage
            {
                get;
                private set;
            }

            [JsonProperty(PropertyName="html")]
            public string Html
            {
                get;
                private set;
            }
        }
        /// <summary>
        /// 
        /// </summary>
        internal enum ProcessTypeEnum
        {
            TonalityMarking,
            Digest,
        }
        /// <summary>
        /// 
        /// </summary>
        private enum OutputTypeEnum
        {
            Xml,
            Xml_Custom,
            Html_FinalTonality,
            Html_FinalTonalityDividedSentence,
            Html_ToplevelTonality,
            Html_ToplevelTonalityDividedSentence,
            Html_BackcolorAllTonality,
        }
        /// <summary>
        /// 
        /// </summary>
        private struct LocalParams
        {
            public LocalParams( HttpContext context ) : this()
            {
                Context = context;
            }

            public HttpContext Context { get; private set; }
            public string Text { get; set; }
            public ProcessTypeEnum ProcessType { get; set; }
            public OutputTypeEnum OutputType { get; set; }
            public string InquiryText { get; set; }
            public ObjectAllocateMethod? ObjectAllocateMethod { get; set; }
        }

#if WITH_OM_TM
        private static readonly Lazy< ILingvisticsServer > _LingvisticsServer;

        static RESTProcessHandler()
        {
            _LingvisticsServer = new Lazy< ILingvisticsServer >( () => 
                LingvisticsServer.Create( Config.LINGUISTICS_SERVER_URL, Config.LINGUISTICS_SERVER_TIMEOUT_IN_SECONDS ), true );

            if ( !Config.LINGUISTICS_SERVER_EXE_FILE_LOCATION.IsNullOrWhiteSpace() )
            {
                Task.Factory.StartNew( StartAndWatchLinguisticsServer, TaskCreationOptions.LongRunning );
            }            
        }

        private static void StartAndWatchLinguisticsServer()
        {
            if ( Config.LINGUISTICS_SERVER_EXE_FILE_LOCATION.IsNullOrWhiteSpace() )
            {
                return;
            }

            try
            {
                var processName = Path.GetFileNameWithoutExtension( Config.LINGUISTICS_SERVER_EXE_FILE_LOCATION );
                var process = Process.GetProcessesByName( processName ).FirstOrDefault();
                if ( process == null )
                {
                    process = Process.Start( Config.CreateLinguisticsServerProcessStartInfo() );
                }

                const int MAX_NOT_ZERO_EXIT_CODES_COUNT_IN_ROW = 10;

                for ( var notZeroExitCodesCount = 0; notZeroExitCodesCount < MAX_NOT_ZERO_EXIT_CODES_COUNT_IN_ROW;  )
                {
                    try
                    {
                        process.WaitForExit();
                        if ( process.HasExited && (process.ExitCode != 0) )
                        {
                            notZeroExitCodesCount++;
                        }
                        else
                        {
                            notZeroExitCodesCount = 0;
                        }
                        Thread.Sleep( 1000 );

                        process = Process.GetProcessesByName( processName ).FirstOrDefault();
                        if ( process == null )
                        {
                            process = Process.Start( Config.CreateLinguisticsServerProcessStartInfo() );
                        }
                    }
                    catch ( Exception ex )
                    {
                        Debug.WriteLine( ex );
                    }
                }               
            }
            catch ( Exception ex )
            {
                Debug.WriteLine( ex );
            }
        }
#endif

        public bool IsReusable
        {
            get { return (true); }
        }

        public void ProcessRequest( HttpContext context )
        {
            try
            {
                #region [.anti-bot.]
                var antiBot = context.ToAntiBot();
                if ( antiBot.IsNeedRedirectOnCaptchaIfRequestNotValid() )
                {
                    antiBot.SendGotoOnCaptchaJsonResponse();
                    return;
                }
                #endregion

                var lp = new LocalParams( context )
                {
                    Text                 = context.GetRequestStringParam( "text", Config.MAX_INPUTTEXT_LENGTH ),
                    ProcessType          = context.Request[ "processType" ].TryConvert2Enum< ProcessTypeEnum >().GetValueOrDefault( ProcessTypeEnum.TonalityMarking ),
                    OutputType           = (context.Request[ "splitBySentences" ].TryToBool( false ) ? OutputTypeEnum.Html_FinalTonalityDividedSentence : OutputTypeEnum.Html_FinalTonality),
                    InquiryText          = context.GetRequestStringParam( "inquiryText", Config.MAX_INPUTTEXT_LENGTH ),
                    ObjectAllocateMethod = context.Request[ "objectAllocateMethod" ].TryToEnum< ObjectAllocateMethod >(),
                };

                #region [.anti-bot.]
                antiBot.MarkRequestEx( lp.Text );
                #endregion

                var html = GetResultHtml( lp );

                context.Response.SendJson( html ); 
            }
            catch ( Exception ex )
            {
                context.Response.SendJson( ex );
            }
        }

#if WITH_OM_TM
        private static string GetResultHtml( LocalParams lp )
        {
			var lingvisticsInput = new LingvisticsTextInput()
			{
				Text                 = lp.Text,
				AfterSpellChecking   = false,
				BaseDate             = DateTime.Now,
				Mode                 = SelectEntitiesMode.Full,
				GenerateAllSubthemes = false, 
			};

            #region [.result.]
            switch ( lp.ProcessType )
            {
                case ProcessTypeEnum.Digest:
                #region [.code.]
                {
                    lingvisticsInput.Options = LingvisticsResultOptions.OpinionMiningWithTonality;

                    var lingvisticResult = _LingvisticsServer.Value.ProcessText( lingvisticsInput );

                    var html = ConvertToHtml( lp.Context, lingvisticResult.OpinionMiningWithTonalityResult );
                    return (html);
                }
                #endregion

                case ProcessTypeEnum.TonalityMarking:
                #region [.code.]
                {
                    lingvisticsInput.TonalityMarkingInput = new TonalityMarkingInputParams4InProcess();
                    if ( !lp.InquiryText.IsNullOrWhiteSpace() )
                    {
                        lingvisticsInput.TonalityMarkingInput.InquiriesSynonyms = lp.InquiryText.ToTextList();
                    }
                    lingvisticsInput.ObjectAllocateMethod = lp.ObjectAllocateMethod.GetValueOrDefault( ObjectAllocateMethod.FirstVerbEntityWithRoleObj );
                    lingvisticsInput.Options = LingvisticsResultOptions.Tonality;                    

                    var lingvisticResult = _LingvisticsServer.Value.ProcessText( lingvisticsInput );

                    var html = ConvertToHtml( lp.Context, lingvisticResult.TonalityResult, lp.OutputType );
                    return (html);
                }
                #endregion

                default:
                    throw (new ArgumentException( lp.ProcessType.ToString() ));
            }
            #endregion
        }
#else
        private static string GetResultHtml( LocalParams lp )
        {
            switch ( lp.ProcessType )
            {
                case ProcessTypeEnum.Digest:
                #region [.code.]
                {
                    var inputParams = new DigestInputParams( lp.Text, InputTextFormat.PlainText ) { ExecuteTonalityMarking = true };
                    if ( !lp.InquiryText.IsNullOrWhiteSpace() )
                    {
                        inputParams.InquiriesSynonyms = lp.InquiryText.ToTextList();
                    }
                    if ( lp.ObjectAllocateMethod.HasValue )
                    {
                        inputParams.ObjectAllocateMethod = lp.ObjectAllocateMethod.Value;
                    }
                    var html = GetDigestResultHtml( lp.Context, inputParams );

                    return (html);
                } 
                #endregion

                case ProcessTypeEnum.TonalityMarking:
                #region [.code.]
                {
                    var inputParams = new TonalityMarkingInputParams( lp.Text );
                    if ( !lp.InquiryText.IsNullOrWhiteSpace() )
                    {
                        inputParams.InquiriesSynonyms = lp.InquiryText.ToTextList();
                    }
                    if ( lp.ObjectAllocateMethod.HasValue )
                    {
                        inputParams.ObjectAllocateMethod = lp.ObjectAllocateMethod.Value;
                    }
                    var html = GetTonalityMarkingResultHtml( lp.Context, inputParams, lp.OutputType );

                    return (html);
                } 
                #endregion

                default:
                    throw (new ArgumentException( lp.ProcessType.ToString() ));
            }
        }

        private static string GetTonalityMarkingResultHtml( HttpContext context, TonalityMarkingInputParams inputParams, OutputTypeEnum outputType )
        {
            var result = default(TonalityMarkingOutputResult);
            using ( var client = new TonalityMarkingWcfClient( Config.TONALITY_MARKING_ENDPOINT_CONFIGURATION_NAME ) )
            {
                result = client.ExecuteTonalityMarking( inputParams );
            }

            var html = ConvertToHtml( context, result, outputType );
            return (html);
        }

        private static string GetDigestResultHtml( HttpContext context, DigestInputParams inputParams )
        {
            var result = default(DigestOutputResult);
            using ( var client = new DigestWcfClient( Config.DIGEST_ENDPOINT_CONFIGURATION_NAME ) )
            {
                result = client.ExecuteDigest( inputParams );
            }

            var html = ConvertToHtml( context, result );
            return (html);
        }
#endif
        private static string ConvertToHtml( HttpContext context, TonalityMarkingOutputResult result, OutputTypeEnum outputType )
        {
            var xdoc = new XmlDocument(); 
            xdoc.LoadXml( result.OutputXml );

            var xslt = new XslCompiledTransform( false );

            var xsltFilename = default(string);
            switch ( outputType )
            {
                case OutputTypeEnum.Xml_Custom:
                    xsltFilename = "Xml.xslt";
                    break;
                case OutputTypeEnum.Html_ToplevelTonality:
                    xsltFilename = "ToplevelTonality.xslt"; 
                    break;
                case OutputTypeEnum.Html_ToplevelTonalityDividedSentence:
                    xsltFilename = "ToplevelTonalityDividedSentence.xslt";
                    break;
                case OutputTypeEnum.Html_FinalTonality:
                    xsltFilename = "FinalTonality.xslt"; 
                    break;
                case OutputTypeEnum.Html_FinalTonalityDividedSentence:
                    xsltFilename = "FinalTonalityDividedSentence.xslt";
                    break;
                case OutputTypeEnum.Html_BackcolorAllTonality:
                    xsltFilename = "BackcolorAllTonality.xslt"; 
                    break;
                default:
                    throw (new ArgumentException(outputType.ToString()));
            }

            xslt.Load( context.Server.MapPath( "~/App_Data/" + xsltFilename ) );

            var sb = new StringBuilder();
            using ( var sw = new StringWriter( sb ) )
            {
                xslt.Transform( xdoc.CreateNavigator(), null, sw );
            }
            return (sb.ToString());
        }

        private static string ConvertToHtml( HttpContext context, DigestOutputResult result )
        {
            const string ANYTHING_ISNT_PRESENT = "<span style='color: maroon; font-size: x-small;'>[Ничего нет.]</span>";
            const string TABLE_START           = "<table border='1' style='font-size: x-small;'><tr><th>#</th><th>SUBJECT</th><th>OBJECT</th><th>SENTENCE</th></tr>";
            const string TABLE_END             = "</table>";
            const string TABLE_ROW_FORMAT      = "<tr valign='top'><td>{0}</td><td>&nbsp;{1}</td><td>&nbsp;{2}</td><td style='padding: 3px;'>{3}</td></tr>";

            if ( !result.Tuples.Any() )
            {
                return (ANYTHING_ISNT_PRESENT);
            }                

            const string XSLT_FILENAME = "FinalTonality.Digest.test.xslt";

            var xslt = new XslCompiledTransform( false );
            xslt.Load( context.Server.MapPath( "~/App_Data/" + XSLT_FILENAME ) );

            var tmp = new StringBuilder();
            var sb = new StringBuilder( TABLE_START );
            var number = 0;
            foreach ( var tuple in result.Tuples )
            {
                sb.AppendFormat
                (
                TABLE_ROW_FORMAT,
                (++number),
                tuple.Subject.ToHtml(),
                tuple.Object .ToHtml(),
                tuple.GetSentence().Transform( xslt, tmp )
                );
            }
            sb.Append( TABLE_END );

            return (sb.ToString());
        }
    }

    /// <summary>
    /// 
    /// </summary>
    internal static class Extensions
    {
        public static bool TryToBool( this string value, bool defaultValue )
        {
            bool result;
            return (bool.TryParse( value, out result ) ? result : defaultValue);
        }
        public static bool? TryToBool( this string value )
        {
            bool result;
            return (bool.TryParse( value, out result ) ? result : ((bool?) null));
        }

        public static T ToEnum< T >( this string value ) where T : struct
        {
            var result = (T) Enum.Parse( typeof(T), value, true );
            return (result);
        }
        public static T? TryToEnum< T >( this string value ) where T : struct
        {
            T t;
            return (Enum.TryParse( value, true, out t ) ? t : ((T?) null));
        }
        public static int ToInt32( this string value )
        {
            return (int.Parse( value ));
        }

        public static string GetRequestStringParam( this HttpContext context, string paramName, int maxLength )
        {
            var value = context.Request[ paramName ];
            if ( (value != null) && (maxLength < value.Length) && (0 < maxLength) )
            {
                return (value.Substring( 0, maxLength ));
            }
            return (value);
        }

        public static bool IsNullOrWhiteSpace( this string value )
        {
            return (string.IsNullOrWhiteSpace( value ));
        }
        public static List< string > ToTextList( this string text )
        {
            return (text.Split( new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries ).ToList());
        }


        public static string ToHtml( this SubjectEssence subject )
        {
            return (subject.IsAuthor ? "<span style='color: silver;'>{0}</span>" : "{0}").FormatEx( subject.ToString() );
        }
        public static string ToHtml( this ObjectEssence @object )
        {
            return ((@object != null) ? (@object + ((@object.IsSubjectIndeed) ? "<span style='color: silver;'>&nbsp;(субъект-как-объект)</span>" : string.Empty)) : "-");
        }

        public static string Transform( this XElement xe, XslCompiledTransform xslt, StringBuilder sb )
        {
            if ( sb == null ) sb = new StringBuilder();
            sb.Clear();

            using ( var sw = new StringWriter( sb ) )
            {
                xslt.Transform( xe.CreateReader(), null, sw );
            }
            return (sb.ToString());
        }


        public static void SendJson( this HttpResponse response, string html )
        {
            response.SendJson( new RESTProcessHandler.Result( html ) );
        }
        public static void SendJson( this HttpResponse response, Exception ex )
        {
            response.SendJson( new RESTProcessHandler.Result( ex ) );
        }
        public static void SendJson( this HttpResponse response, RESTProcessHandler.Result result )
        {
            response.ContentType = "application/json"; //"text/html"
            //---response.Headers.Add( "Access-Control-Allow-Origin", "*" );

            var json = JsonConvert.SerializeObject( result );
            response.Write( json );
        }
    }
}