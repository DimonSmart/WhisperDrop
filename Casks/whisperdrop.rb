cask "whisperdrop" do
  arch arm: "arm64", intel: "x64"

  version "0.1.0"

  sha256 arm: "324a99817b8fb3a65806e2c9837570c2683306acee77eabf4ef00ed48a6de9d2",
         intel: "de00d437fa87f0f105ab52b875eebbc7145a7871f595753e618c77e318cdfe40"

  url "https://github.com/DimonSmart/WhisperDrop/releases/download/v#{version}/WhisperDrop-v#{version}-osx-#{arch}-app.zip"

  name "WhisperDrop"
  desc "Local desktop speech-to-text application powered by Whisper"
  homepage "https://github.com/DimonSmart/WhisperDrop"

  app "WhisperDrop.app"

  caveats <<~EOS
    WhisperDrop is currently unsigned and not notarized.
    On first launch macOS may require using Open from the Finder context menu.
  EOS
end

