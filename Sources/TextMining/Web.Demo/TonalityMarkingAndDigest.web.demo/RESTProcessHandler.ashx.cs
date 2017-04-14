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

using captcha;
using Digest;
using Newtonsoft.Json;
using OpinionMining;
using TextMining.Core;
using TonalityMarking;

namespace TonalityMarkingAndDigest.web.demo
{
    /// <summary>
    /// 
    /// </summary>
    internal static class Config
    {
        public static readonly string TONALITY_MARKING_ENDPOINT_CONFIGURATION_NAME = ConfigurationManager.AppSettings[ "TONALITY_MARKING_ENDPOINT_CONFIGURATION_NAME" ];
        public static readonly string DIGEST_ENDPOINT_CONFIGURATION_NAME           = ConfigurationManager.AppSettings[ "DIGEST_ENDPOINT_CONFIGURATION_NAME" ];        

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
        private sealed class Result
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
        private enum ProcessTypeEnum
        {
            TonalityMarking,
            Digest,
        }

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

                var text        = context.GetRequestStringParam( "text", Config.MAX_INPUTTEXT_LENGTH );                
                var processType = context.Request[ "processType" ].TryConvert2Enum< ProcessTypeEnum >().GetValueOrDefault( ProcessTypeEnum.TonalityMarking );
                var outputType  = (context.Request[ "splitBySentences" ].TryToBool( false ) ? OutputTypeEnum.Html_FinalTonalityDividedSentence : OutputTypeEnum.Html_FinalTonality);
                var inquiryText = context.GetRequestStringParam( "inquiryText", Config.MAX_INPUTTEXT_LENGTH );
                var objectAllocateMethod = context.Request[ "objectAllocateMethod" ].TryToEnum< ObjectAllocateMethod >();                

                #region [.anti-bot.]
                antiBot.MarkRequestEx( text );
                #endregion

                switch ( processType )
                {
                    case ProcessTypeEnum.Digest:
                    #region [.code.]
                    {
                        var inputParams = new DigestInputParams( text, InputTextFormat.PlainText ) { ExecuteTonalityMarking = true };
                        if ( !inquiryText.IsNullOrWhiteSpace() )
                        {
                            inputParams.InquiriesSynonyms = inquiryText.ToTextList();
                        }
                        if ( objectAllocateMethod.HasValue )
                        {
                            inputParams.ObjectAllocateMethod = objectAllocateMethod.Value;
                        }
                        var html = GetDigestResultHtml( context, inputParams );

                        SendJsonResponse( context, html );
                    } 
                    #endregion
                    break;

                    case ProcessTypeEnum.TonalityMarking:
                    #region [.code.]
                    {
                        var inputParams = new TonalityMarkingInputParams( text );
                        if ( !inquiryText.IsNullOrWhiteSpace() )
                        {
                            inputParams.InquiriesSynonyms = inquiryText.ToTextList();
                        }
                        if ( objectAllocateMethod.HasValue )
                        {
                            inputParams.ObjectAllocateMethod = objectAllocateMethod.Value;
                        }
                        var html = GetTonalityMarkingResultHtml( context, inputParams, outputType );

                        SendJsonResponse( context, html );
                    } 
                    #endregion
                    break;

                    default:
                        throw (new ArgumentException( processType.ToString() ));
                }                
            }
            catch ( Exception ex )
            {
                SendJsonResponse( context, ex );
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
        private static string ConvertToHtml( HttpContext context, DigestOutputResult result )
        {
            const string ANYTHING_ISNT_PRESENT = "<span style='color: maroon; font-size: X-Small;'>[Ничего нет.]</span>";
            const string TABLE_START           = "<table border='1' style='font-size: X-Small;'><tr><th>#</th><th>SUBJECT</th><th>OBJECT</th><th>SENTENCE</th></tr>";
            const string TABLE_END             = "</table>";
            const string TABLE_ROW_FORMAT      = "<tr valign='top'><td>{0}</td><td>&nbsp;{1}</td><td>&nbsp;{2}</td><td style='padding: 3px;'>{3}</td></tr>";

            if ( !result.Tuples.Any() )
            {
                return (ANYTHING_ISNT_PRESENT);
            }                

            const string xsltFilename = "FinalTonality.Digest.test.xslt";

            var xslt = new XslCompiledTransform( false );
            xslt.Load( context.Server.MapPath( "~/App_Data/" + xsltFilename ) );

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

        private static void SendJsonResponse( HttpContext context, string html )
        {
            SendJsonResponse( context, new Result( html ) );
        }
        private static void SendJsonResponse( HttpContext context, Exception ex )
        {
            SendJsonResponse( context, new Result( ex ) );
        }
        private static void SendJsonResponse( HttpContext context, Result result )
        {
            context.Response.ContentType = "application/json"; //"text/html"
            //---context.Response.Headers.Add( "Access-Control-Allow-Origin", "*" );

            var json = JsonConvert.SerializeObject( result );
            context.Response.Write( json );
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
            if ( sb != null )
            {
                sb.Clear();
            }
            else
            {
                sb = new StringBuilder();
            }
            using ( var sw = new StringWriter( sb ) )
            {
                xslt.Transform( xe.CreateReader(), null, sw );
            }
            return (sb.ToString());
        }
    }
}