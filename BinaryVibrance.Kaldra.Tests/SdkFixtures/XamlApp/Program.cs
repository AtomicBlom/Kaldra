// Builds the view from its XAML and prints what the typed x:Name field holds, so a test can see the generated tree run.
var view = new XamlApp.Views.HelloView();
Console.WriteLine(view.Greeting.Text);
