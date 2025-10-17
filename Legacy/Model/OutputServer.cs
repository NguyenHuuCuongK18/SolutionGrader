namespace SolutionGrader.Legacy.Model
{
    public class OutputServer
    {
        public int Stage { get; set; }
        public string Method { get; set; } = string.Empty;
        public string DataRequest { get; set; } = string.Empty;
        public string Output { get; set; } = string.Empty;
        public string DataTypeMiddleware { get; set; } = string.Empty;
        public int ByteSize { get; set; }

    }
}
