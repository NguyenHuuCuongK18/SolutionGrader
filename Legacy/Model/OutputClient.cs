namespace SolutionGrader.Legacy.Model
{
    public class OutputClient
    {
        public int Stage { get; set; }
        public string Method { get; set; } = string.Empty;
        public string DataResponse { get; set; } = string.Empty;
        public int StatusCode { get; set; }
        public string Output { get; set; } = string.Empty;
        public string DataTypeMiddleWare { get; set; } = string.Empty;
        public int ByteSize { get; set; }
    }
}
