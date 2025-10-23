namespace SolutionGrader.Legacy.Model
{
    public class OutputClient
    {
        public int Stage { get; set; }
        public string Method { get; set; } = string.Empty;
        public string DataResponse { get; set; } = string.Empty;
        public string StatusCode { get; set; } = string.Empty;
        public string Output { get; set; } = string.Empty;
        public string DataTypeMiddleWare { get; set; } = string.Empty;
        public string ByteSize { get; set; } = string.Empty;
    }
}
