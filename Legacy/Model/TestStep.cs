namespace SolutionGrader.Legacy.Model
{
    public class TestStep
    {
        public int StepNumber { get; set; }          // Step 1, Step 2,...
        public string ClientInput { get; set; }      // Input gửi từ client
        public string ClientOutput { get; set; }     // Output client nhận
        public string ServerOutput { get; set; }     // Output server trả về
    }
}
